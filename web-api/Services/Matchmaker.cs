using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Uvc.Core.Models;

namespace Uvc.Server.Services
{
    public record MatchCandidate(string ConnectionId, WebSocket Socket, string Sender, string Role, string Mode);

    public class Matchmaker
    {
        private readonly ConcurrentQueue<MatchCandidate> _queueHostHost = new();
        private readonly ConcurrentQueue<MatchCandidate> _queueHostFan = new();
        private readonly ConcurrentQueue<MatchCandidate> _queueFanHost = new();
        private readonly ConcurrentQueue<MatchCandidate> _queueFanFan = new();

        private readonly RoomManager _roomManager;

        public Matchmaker(RoomManager roomManager)
        {
            _roomManager = roomManager;
        }

        public RoomSession? Enqueue(MatchCandidate candidate)
        {
            MatchCandidate? opponent = null;

            if (candidate.Role == PersonaRole.Host)
            {
                // 主播尋同業 (Host vs Host)
                if (candidate.Mode == "host_host" || candidate.Mode == "werewolf")
                {
                    while (_queueHostHost.TryDequeue(out opponent))
                    {
                        if (opponent.Socket.State == WebSocketState.Open && opponent.ConnectionId != candidate.ConnectionId)
                        {
                            return _roomManager.CreateRoom(candidate, opponent, "Host_Host_Room");
                        }
                    }
                    _queueHostHost.Enqueue(candidate);
                }
                else
                {
                    // 主播找觀眾 (與 Fan_Host 對流)
                    while (_queueFanHost.TryDequeue(out opponent))
                    {
                        if (opponent.Socket.State == WebSocketState.Open && opponent.ConnectionId != candidate.ConnectionId)
                        {
                            return _roomManager.CreateRoom(candidate, opponent, "Host_Fan_Room");
                        }
                    }
                    _queueHostFan.Enqueue(candidate);
                }
            }
            else // Audience
            {
                // 同好同溫層 (Fan vs Fan)
                if (candidate.Mode == "fan_fan" || candidate.Mode == "battleroyale")
                {
                    while (_queueFanFan.TryDequeue(out opponent))
                    {
                        if (opponent.Socket.State == WebSocketState.Open && opponent.ConnectionId != candidate.ConnectionId)
                        {
                            return _roomManager.CreateRoom(candidate, opponent, "Fan_Fan_Room");
                        }
                    }
                    _queueFanFan.Enqueue(candidate);
                }
                else
                {
                    // 觀眾抽盲盒 (與 Host_Fan 對流)
                    while (_queueHostFan.TryDequeue(out opponent))
                    {
                        if (opponent.Socket.State == WebSocketState.Open && opponent.ConnectionId != candidate.ConnectionId)
                        {
                            return _roomManager.CreateRoom(opponent, candidate, "Host_Fan_Room");
                        }
                    }
                    _queueFanHost.Enqueue(candidate);
                }
            }

            return null;
        }

        public void ClearQueues()
        {
            while (_queueHostHost.TryDequeue(out _)) { }
            while (_queueHostFan.TryDequeue(out _)) { }
            while (_queueFanHost.TryDequeue(out _)) { }
            while (_queueFanFan.TryDequeue(out _)) { }
        }
    }
}
