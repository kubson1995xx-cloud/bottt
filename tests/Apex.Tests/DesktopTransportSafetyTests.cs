using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Apex.Desktop.Bridge;
using Xunit;

namespace Apex.Tests
{
    public sealed class DesktopTransportSafetyTests
    {
        private sealed class HungCloseSocket : WebSocket
        {
            private WebSocketState _state = WebSocketState.Open;
            public bool Aborted { get; private set; }
            public override WebSocketCloseStatus? CloseStatus => null;
            public override string? CloseStatusDescription => null;
            public override WebSocketState State => _state;
            public override string? SubProtocol => null;
            public override void Abort() { Aborted = true; _state = WebSocketState.Aborted; }
            public override void Dispose() => Abort();
            public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        [Fact]
        public async Task CloseCannotWaitForeverForPeerAndAlwaysAborts()
        {
            using var ws = new HungCloseSocket();
            var transport = new WebSocketTransport(ws, "test");
            await transport.CloseAsync("stop").WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(ws.Aborted);
        }

        private sealed class ClosingTransport : IBridgeTransport
        {
            public string Kind => "test";
            public string Remote => "memory";
            public int CloseCount;
            public readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task SendAsync(string json, CancellationToken ct) => Task.CompletedTask;
            public Task CloseAsync(string reason) { CloseCount++; return Closed.Task; }
        }

        [Fact]
        public async Task SessionCloseIsOneSharedCompletionBarrier()
        {
            var transport = new ClosingTransport();
            var session = new BridgeSession(transport);
            Task first = session.CloseAsync("reject");
            Task second = session.CloseAsync("server finally");
            Assert.Same(first, second);
            Assert.Equal(1, transport.CloseCount);
            transport.Closed.SetResult();
            await first;
        }

        [Fact]
        public async Task ClosedSocketCannotReportSuccessfulSend()
        {
            using var ws = new HungCloseSocket(); ws.Abort();
            var transport = new WebSocketTransport(ws, "test");
            await Assert.ThrowsAsync<WebSocketException>(() => transport.SendAsync("{}", CancellationToken.None));
        }
    }
}
