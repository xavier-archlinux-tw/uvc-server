using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Uvc.Core.Models;

namespace Uvc.Server.Services
{
    public class RoomMember
    {
        public string ConnectionId { get; set; } = string.Empty;
        public WebSocket Socket { get; set; } = null!;
        public string Sender { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public long LastSentTimestamp { get; set; }
    }

    public class RoomSession
    {
        public string RoomId { get; set; } = string.Empty;
        public string RoomType { get; set; } = string.Empty;
        public List<RoomMember> Members { get; set; } = new();
        public List<NetworkPacket> RingBuffer { get; } = new();
        public object LockObj { get; } = new();

        public void AddToRingBuffer(NetworkPacket packet)
        {
            lock (LockObj)
            {
                if (RingBuffer.Count >= 50)
                {
                    RingBuffer.RemoveAt(0);
                }
                RingBuffer.Add(packet);
            }
        }
    }

    public class RoomManager
    {
        private readonly ConcurrentDictionary<string, RoomSession> _rooms = new();
        private readonly ConcurrentDictionary<string, string> _connectionToRoom = new();

        public int ActiveRoomsCount => _rooms.Count;

        public RoomSession CreateRoom(MatchCandidate c1, MatchCandidate c2, string roomType)
        {
            var roomId = "room_" + Guid.NewGuid().ToString("N")[..8];
            var session = new RoomSession
            {
                RoomId = roomId,
                RoomType = roomType
            };

            var m1 = new RoomMember { ConnectionId = c1.ConnectionId, Socket = c1.Socket, Sender = c1.Sender, Role = c1.Role };
            var m2 = new RoomMember { ConnectionId = c2.ConnectionId, Socket = c2.Socket, Sender = c2.Sender, Role = c2.Role };

            session.Members.Add(m1);
            session.Members.Add(m2);

            _rooms[roomId] = session;
            _connectionToRoom[c1.ConnectionId] = roomId;
            _connectionToRoom[c2.ConnectionId] = roomId;

            return session;
        }

        public RoomSession? GetRoomByConnectionId(string connectionId)
        {
            if (_connectionToRoom.TryGetValue(connectionId, out var roomId) && _rooms.TryGetValue(roomId, out var session))
            {
                return session;
            }
            return null;
        }

        public (bool allowed, long deltaMs) CheckRateLimit(string connectionId, string role)
        {
            // 主播無冷卻限制
            if (role == PersonaRole.Host)
            {
                return (true, 0);
            }

            // 觀眾 0.7s (700ms) 防刷閥
            var room = GetRoomByConnectionId(connectionId);
            if (room == null) return (true, 0);

            lock (room.LockObj)
            {
                var member = room.Members.Find(m => m.ConnectionId == connectionId);
                if (member == null) return (true, 0);

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var delta = now - member.LastSentTimestamp;
                if (delta < 700)
                {
                    return (false, delta);
                }

                member.LastSentTimestamp = now;
                return (true, delta);
            }
        }

        public async Task BroadcastToRoomAsync(RoomSession room, NetworkPacket packet, CancellationToken ct = default)
        {
            room.AddToRingBuffer(packet);

            var json = JsonSerializer.Serialize(packet);
            var bytes = Encoding.UTF8.GetBytes(json);
            var segment = new ArraySegment<byte>(bytes);

            List<Task> sendTasks = new();
            lock (room.LockObj)
            {
                foreach (var member in room.Members)
                {
                    if (member.Socket.State == WebSocketState.Open)
                    {
                        sendTasks.Add(member.Socket.SendAsync(segment, WebSocketMessageType.Text, true, ct));
                    }
                }
            }

            await Task.WhenAll(sendTasks);
        }

        public async Task LeaveRoomAsync(string connectionId, CancellationToken ct = default)
        {
            if (_connectionToRoom.TryRemove(connectionId, out var roomId) && _rooms.TryGetValue(roomId, out var room))
            {
                RoomMember? leavingMember = null;
                lock (room.LockObj)
                {
                    leavingMember = room.Members.Find(m => m.ConnectionId == connectionId);
                    room.Members.RemoveAll(m => m.ConnectionId == connectionId);
                }

                if (leavingMember != null)
                {
                    var notice = new NetworkPacket(
                        PacketAction.LeaveRoom,
                        leavingMember.Sender,
                        leavingMember.Role,
                        $"成員 {leavingMember.Sender} 已離開房間"
                    );

                    await BroadcastToRoomAsync(room, notice, ct);
                }

                if (room.Members.Count == 0)
                {
                    _rooms.TryRemove(roomId, out _);
                }
            }
        }
    }
}
