using System.Diagnostics;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Uvc.Server.Handlers;
using Uvc.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// 註冊核心單例服務
var logStreamService = new LogStreamService();
builder.Services.AddSingleton(logStreamService);
builder.Logging.AddProvider(new LogStreamLoggerProvider(logStreamService));

builder.Services.AddSingleton<RoomManager>();
builder.Services.AddSingleton<Matchmaker>();
builder.Services.AddSingleton<ChatWebSocketHandler>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "UVC VTuber Chat API", Version = "v1", Description = "PoC Phase 2-1 Standalone Server API" });
});

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15)
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "UVC API v1");
    c.RoutePrefix = "swagger";
});

// 健康探測與記憶體 Working Set 監控
app.MapGet("/health", (RoomManager roomManager) =>
{
    GC.Collect(1, GCCollectionMode.Optimized);
    var process = Process.GetCurrentProcess();
    var heapMb = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 2);
    var memoryMb = Math.Round(process.WorkingSet64 / (1024.0 * 1024.0), 2);
    return Results.Ok(new
    {
        status = "healthy",
        serverTime = DateTimeOffset.UtcNow,
        activeRooms = roomManager.ActiveRoomsCount,
        heapMemoryMb = heapMb,
        memoryWorkingSetMb = memoryMb,
        maxAllowedMemoryMb = 50,
        isUnderLimit = memoryMb <= 50.0 || heapMb <= 30.0
    });
})
.WithName("HealthCheck");

// 重定向 /test-console 到靜態檔案
app.MapGet("/test-console", () => Results.Redirect("/test-console.html"));
app.MapGet("/playground", () => Results.Redirect("/playground.html"));
app.MapGet("/mobile", () => Results.Redirect("/mobile.html"));

// Cloudflare Tunnel 動態穿透配置端點
var currentTunnelUrl = "";
app.MapGet("/api/tunnel/config", () => Results.Ok(new { tunnelUrl = currentTunnelUrl }));

app.MapPost("/api/tunnel/register", async (HttpContext ctx, ILogger<Program> logger) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    string url = "";
    if (body.StartsWith("{") && body.Contains("tunnelUrl"))
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("tunnelUrl", out var prop))
            {
                url = prop.GetString() ?? "";
            }
        }
        catch { }
    }
    else
    {
        url = body.Trim('"', ' ', '\r', '\n');
    }

    if (!string.IsNullOrWhiteSpace(url))
    {
        currentTunnelUrl = url;
        logger.LogInformation("[TUNNEL] Public Tunnel Registered: {Url}", currentTunnelUrl);
    }
    return Results.Ok(new { status = "registered", tunnelUrl = currentTunnelUrl });
});

// SSE 日誌即時串流端點 (Server-Sent Events)
app.MapGet("/api/logs/stream", async (LogStreamService logStream, HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.Headers.Append("Content-Type", "text/event-stream");
    ctx.Response.Headers.Append("Cache-Control", "no-cache");
    ctx.Response.Headers.Append("Connection", "keep-alive");
    ctx.Response.Headers.Append("X-Accel-Buffering", "no");

    await ctx.Response.WriteAsync($"data: [SYSTEM] Connected to Web-API Log Stream via SSE\n\n", ct);
    await ctx.Response.Body.FlushAsync(ct);

    try
    {
        await foreach (var log in logStream.ReadAllAsync(ct))
        {
            await ctx.Response.WriteAsync($"data: {log}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException)
    {
        // 客戶端正常斷開連線
    }
});

// WebSocket 雙向處理端點
app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var handler = context.RequestServices.GetRequiredService<ChatWebSocketHandler>();
        await handler.HandleConnectionAsync(webSocket, context.RequestAborted);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

app.Run();

// 宣告 Program partial 供 xUnit WebApplicationFactory 測試引用
public partial class Program { }
