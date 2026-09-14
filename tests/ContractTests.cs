using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Uvc.Core.Models;
using Uvc.Server.Services;
using Xunit;

// 關閉 xUnit 測試並行，防止多測試並發爭搶記憶體配對佇列
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Uvc.Server.Tests
{
    public class ContractTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _factory;

        public ContractTests(WebApplicationFactory<Program> factory)
        {
            _factory = factory;
            // 清空記憶體佇列避免跨測試污染
            var matchmaker = _factory.Services.GetService<Matchmaker>();
            matchmaker?.ClearQueues();
        }

        private async Task<WebSocket> CreateClientWebSocketAsync()
        {
            var wsClient = _factory.Server.CreateWebSocketClient();
            var wsUri = new Uri(_factory.Server.BaseAddress, "/ws");
            return await wsClient.ConnectAsync(wsUri, CancellationToken.None);
        }

        private static async Task SendPacketAsync(WebSocket socket, NetworkPacket packet)
        {
            var json = JsonSerializer.Serialize(packet);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static async Task<NetworkPacket?> ReceivePacketAsync(WebSocket socket, int timeoutMs = 5000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var buffer = new byte[4096];
            try
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    return JsonSerializer.Deserialize<NetworkPacket>(json);
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            return null;
        }

        [Fact]
        public async Task Test_WebSocket_Handshake_Success()
        {
            using var socket = await CreateClientWebSocketAsync();
            Assert.Equal(WebSocketState.Open, socket.State);

            await SendPacketAsync(socket, new NetworkPacket(PacketAction.Ping, "Tester", "host", "ping_payload"));
            var response = await ReceivePacketAsync(socket);
            Assert.NotNull(response);
            Assert.Equal(PacketAction.Pong, response!.action);

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }

        [Fact]
        public async Task Test_FourWay_Matchmaking_Pairing()
        {
            using var hostSocket = await CreateClientWebSocketAsync();
            using var fanSocket = await CreateClientWebSocketAsync();

            // 主播發起排隊 (找觀眾)
            await SendPacketAsync(hostSocket, new NetworkPacket(PacketAction.JoinMatch, "一瞬晨星", PersonaRole.Host, "host_fan"));
            await Task.Delay(50);

            // 觀眾發起排隊 (抽盲盒)
            await SendPacketAsync(fanSocket, new NetworkPacket(PacketAction.JoinMatch, "DD死忠粉", PersonaRole.Audience, "random"));

            // 兩端應同步收到 match_found
            var hostResp = await ReceivePacketAsync(hostSocket);
            var fanResp = await ReceivePacketAsync(fanSocket);

            Assert.NotNull(hostResp);
            Assert.NotNull(fanResp);
            Assert.Equal(PacketAction.MatchFound, hostResp!.action);
            Assert.Equal(PacketAction.MatchFound, fanResp!.action);

            await hostSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            await fanSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }

        [Fact]
        public async Task Test_Message_Broadcast_And_VipTag()
        {
            using var hostSocket = await CreateClientWebSocketAsync();
            using var fanSocket = await CreateClientWebSocketAsync();

            await SendPacketAsync(hostSocket, new NetworkPacket(PacketAction.JoinMatch, "HostVIP", PersonaRole.Host, "host_fan"));
            await Task.Delay(50);
            await SendPacketAsync(fanSocket, new NetworkPacket(PacketAction.JoinMatch, "AudienceFan", PersonaRole.Audience, "random"));

            var hMatch = await ReceivePacketAsync(hostSocket);
            var fMatch = await ReceivePacketAsync(fanSocket);
            Assert.NotNull(hMatch);
            Assert.NotNull(fMatch);

            // 主播發送發言
            await SendPacketAsync(hostSocket, new NetworkPacket(PacketAction.SendMsg, "HostVIP", PersonaRole.Host, "各位DD好！"));

            // 主播與觀眾均收到廣播
            var hostMsg = await ReceivePacketAsync(hostSocket);
            var fanMsg = await ReceivePacketAsync(fanSocket);

            Assert.NotNull(hostMsg);
            Assert.NotNull(fanMsg);
            Assert.Equal(PacketAction.BroadcastMsg, fanMsg!.action);
            Assert.Equal("HostVIP", fanMsg.sender);
            Assert.Equal(PersonaRole.Host, fanMsg.role);
            Assert.Equal("各位DD好！", fanMsg.data);

            await hostSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            await fanSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }

        [Fact]
        public async Task Test_Cooldown_Spam_Protection()
        {
            using var hostSocket = await CreateClientWebSocketAsync();
            using var fanSocket = await CreateClientWebSocketAsync();

            await SendPacketAsync(hostSocket, new NetworkPacket(PacketAction.JoinMatch, "HostPlayer", PersonaRole.Host, "host_fan"));
            await Task.Delay(50);
            await SendPacketAsync(fanSocket, new NetworkPacket(PacketAction.JoinMatch, "SpamAudience", PersonaRole.Audience, "random"));

            await ReceivePacketAsync(hostSocket);
            await ReceivePacketAsync(fanSocket);

            // 觀眾發送第一則
            await SendPacketAsync(fanSocket, new NetworkPacket(PacketAction.SendMsg, "SpamAudience", PersonaRole.Audience, "訊息 1"));
            var msg1 = await ReceivePacketAsync(fanSocket);
            Assert.NotNull(msg1);
            Assert.Equal(PacketAction.BroadcastMsg, msg1!.action);

            // 觀眾立即發送第二則 (間隔未滿 700ms)
            await SendPacketAsync(fanSocket, new NetworkPacket(PacketAction.SendMsg, "SpamAudience", PersonaRole.Audience, "訊息 2 (瞬發)"));
            var msg2 = await ReceivePacketAsync(fanSocket);

            Assert.NotNull(msg2);
            Assert.Equal(PacketAction.RateLimited, msg2!.action);

            await hostSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            await fanSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }

        [Fact]
        public async Task Test_RingBuffer_Catchup()
        {
            using var hostSocket = await CreateClientWebSocketAsync();
            using var fanSocket = await CreateClientWebSocketAsync();

            await SendPacketAsync(hostSocket, new NetworkPacket(PacketAction.JoinMatch, "H1", PersonaRole.Host, "host_fan"));
            await Task.Delay(50);
            await SendPacketAsync(fanSocket, new NetworkPacket(PacketAction.JoinMatch, "F1", PersonaRole.Audience, "random"));

            await ReceivePacketAsync(hostSocket);
            await ReceivePacketAsync(fanSocket);

            // 發送 3 則訊息
            for (int i = 0; i < 3; i++)
            {
                await SendPacketAsync(hostSocket, new NetworkPacket(PacketAction.SendMsg, "H1", PersonaRole.Host, $"Msg {i}"));
                await ReceivePacketAsync(hostSocket);
                await ReceivePacketAsync(fanSocket);
            }

            Assert.Equal(WebSocketState.Open, hostSocket.State);
            Assert.Equal(WebSocketState.Open, fanSocket.State);

            await hostSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            await fanSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }

        [Fact]
        public async Task Test_Graceful_Disconnect()
        {
            using var hostSocket = await CreateClientWebSocketAsync();
            using var fanSocket = await CreateClientWebSocketAsync();

            await SendPacketAsync(hostSocket, new NetworkPacket(PacketAction.JoinMatch, "LeavingHost", PersonaRole.Host, "host_fan"));
            await Task.Delay(50);
            await SendPacketAsync(fanSocket, new NetworkPacket(PacketAction.JoinMatch, "RemainingFan", PersonaRole.Audience, "random"));

            await ReceivePacketAsync(hostSocket);
            await ReceivePacketAsync(fanSocket);

            // 主播關閉連線
            await hostSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);

            // 觀眾端收到 leave_room 通知
            var leaveNotice = await ReceivePacketAsync(fanSocket);
            Assert.NotNull(leaveNotice);
            Assert.Equal(PacketAction.LeaveRoom, leaveNotice!.action);

            await fanSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }
    }
}
