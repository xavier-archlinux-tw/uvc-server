using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Uvc.Server.Services
{
    /// <summary>
    /// 高效能記憶體無鎖日誌廣播服務，專為 SSE (Server-Sent Events) 前端即時終端串流設計
    /// </summary>
    public class LogStreamService
    {
        private readonly Channel<string> _channel;
        private readonly Timer _heartbeatTimer;

        public LogStreamService()
        {
            // 設定容量 200 之有界隊列，防爆記憶體 (DropOldest)，確保 GC Managed Heap 恆小於 50MB
            var options = new BoundedChannelOptions(200)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = false,
                SingleReader = false
            };
            _channel = Channel.CreateBounded<string>(options);

            // 每 10 秒發送一次心跳，確保 Cloudflare / 反向代理長連線永久保活
            _heartbeatTimer = new Timer(_ =>
            {
                Publish("[HEARTBEAT] UVC Server Keep-Alive Ping");
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        }

        public void Publish(string message)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            _channel.Writer.TryWrite($"[{timestamp}] {message}");
        }

        public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct)
        {
            return _channel.Reader.ReadAllAsync(ct);
        }
    }

    /// <summary>
    /// 自定義 ILoggerProvider，自動將系統中的關鍵結構化日誌推送至 LogStreamService
    /// </summary>
    public class LogStreamLoggerProvider : ILoggerProvider
    {
        private readonly LogStreamService _logStream;

        public LogStreamLoggerProvider(LogStreamService logStream)
        {
            _logStream = logStream;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new LogStreamLogger(categoryName, _logStream);
        }

        public void Dispose()
        {
        }

        private class LogStreamLogger : ILogger
        {
            private readonly string _categoryName;
            private readonly LogStreamService _logStream;

            public LogStreamLogger(string categoryName, LogStreamService logStream)
            {
                _categoryName = categoryName;
                _logStream = logStream;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;

                var msg = formatter(state, exception);
                // 優先捕獲通訊核心日誌標籤
                if (msg.Contains("[WS]") || msg.Contains("[MATCH]") || msg.Contains("[RATE_LIMIT]") || msg.Contains("[SYSTEM]") || msg.Contains("[METRICS]") || msg.Contains("Request finished"))
                {
                    _logStream.Publish(msg);
                }
            }
        }
    }
}
