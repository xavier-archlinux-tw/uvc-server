using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Uvc.Core.Models;
using Uvc.Server.Services;

namespace Uvc.Server.Handlers
{
    public class ChatWebSocketHandler
    {
        private readonly Matchmaker _matchmaker;
        private readonly RoomManager _roomManager;
        private readonly ILogger<ChatWebSocketHandler> _logger;

        public ChatWebSocketHandler(Matchmaker matchmaker, RoomManager roomManager, ILogger<ChatWebSocketHandler> logger)
        {
            _matchmaker = matchmaker;
            _roomManager = roomManager;
            _logger = logger;
        }

        public async Task HandleConnectionAsync(WebSocket socket, CancellationToken ct)
        {
            var connectionId = "conn_" + Guid.NewGuid().ToString("N")[..8];
            _logger.LogInformation("[WS][CONNECT] Client {ConnectionId} connected", connectionId);

            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            string? currentSender = null;
            string? currentRole = null;

            try
            {
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var rawJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        NetworkPacket? packet = null;
                        try
                        {
                            packet = JsonSerializer.Deserialize<NetworkPacket>(rawJson);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("[WS][PARSE_ERR] {Error} from {ConnectionId}", ex.Message, connectionId);
                            continue;
                        }

                        if (packet == null) continue;

                        currentSender = packet.sender;
                        currentRole = packet.role;

                        await ProcessPacketAsync(connectionId, socket, packet, ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogInformation("[WS][DISCONNECT] {ConnectionId} disconnected: {Message}", connectionId, ex.Message);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                await _roomManager.LeaveRoomAsync(connectionId, CancellationToken.None);
                _logger.LogInformation("[WS][CLEANUP] Connection {ConnectionId} cleaned up", connectionId);

                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    try
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
                    }
                    catch { }
                }
            }
        }

        private async Task ProcessPacketAsync(string connectionId, WebSocket socket, NetworkPacket packet, CancellationToken ct)
        {
            switch (packet.action)
            {
                case PacketAction.JoinMatch:
                    _logger.LogInformation("[MATCH][JOIN] {Sender} ({Role}) queued mode={Mode}", packet.sender, packet.role, packet.data);
                    var candidate = new MatchCandidate(connectionId, socket, packet.sender, packet.role, packet.data ?? "default");
                    var room = _matchmaker.Enqueue(candidate);
                    if (room != null)
                    {
                        _logger.LogInformation("[MATCH][PAIR] Paired room {RoomId} members={Count}", room.RoomId, room.Members.Count);
                        
                        // 下發 match_found 給房間兩端
                        foreach (var m in room.Members)
                        {
                            var matchNotice = new NetworkPacket(
                                PacketAction.MatchFound,
                                "System",
                                "server",
                                JsonSerializer.Serialize(new { roomId = room.RoomId, roomType = room.RoomType, opponent = room.Members.Find(x => x.ConnectionId != m.ConnectionId)?.Sender })
                            );
                            await SendPacketAsync(m.Socket, matchNotice, ct);
                        }
                    }
                    break;

                case PacketAction.SendMsg:
                    var (allowed, delta) = _roomManager.CheckRateLimit(connectionId, packet.role);
                    if (!allowed)
                    {
                        _logger.LogWarning("[RATE_LIMIT] Client {Sender} rejected (delta={Delta}ms < 700ms)", packet.sender, delta);
                        var rateLimitPkt = new NetworkPacket(
                            PacketAction.RateLimited,
                            "System",
                            "server",
                            $"發言過於頻繁，冷卻中 (間隔 {delta}ms < 700ms)"
                        );
                        await SendPacketAsync(socket, rateLimitPkt, ct);
                        return;
                    }

                    var currentRoom = _roomManager.GetRoomByConnectionId(connectionId);
                    if (currentRoom != null)
                    {
                        _logger.LogInformation("[WS][RECV] {Sender} ({Role}) send_msg: \"{Data}\"", packet.sender, packet.role, packet.data);
                        
                        var broadcastPkt = new NetworkPacket(
                            PacketAction.BroadcastMsg,
                            packet.sender,
                            packet.role,
                            packet.data
                        );

                        await _roomManager.BroadcastToRoomAsync(currentRoom, broadcastPkt, ct);
                    }
                    break;

                case PacketAction.Ping:
                    var pongPkt = new NetworkPacket(PacketAction.Pong, "Server", "server", packet.data);
                    await SendPacketAsync(socket, pongPkt, ct);
                    break;

                case PacketAction.LeaveRoom:
                    _logger.LogInformation("[ROOM][LEAVE] {Sender} leaving room", packet.sender);
                    await _roomManager.LeaveRoomAsync(connectionId, ct);
                    break;
            }
        }

        private static async Task SendPacketAsync(WebSocket socket, NetworkPacket packet, CancellationToken ct)
        {
            if (socket.State == WebSocketState.Open)
            {
                var json = JsonSerializer.Serialize(packet);
                var bytes = Encoding.UTF8.GetBytes(json);
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
        }
    }
}
