using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Bridge;
using Apex.Desktop.Runtime;
using Apex.Desktop.Services;
using Microsoft.AspNetCore.Http;

namespace Apex.Desktop.Bridge
{
    /// <summary>Transport WebSocket (używany przez most cTrader – WebSocketClient z API cTrader).</summary>
    public sealed class WebSocketTransport : IBridgeTransport
    {
        private readonly WebSocket _ws;

        public WebSocketTransport(WebSocket ws, string remote)
        {
            _ws = ws;
            Remote = remote;
        }

        public string Kind => "WebSocket";
        public string Remote { get; }

        public Task SendAsync(string json, CancellationToken ct)
        {
            if (_ws.State != WebSocketState.Open) throw new WebSocketException("Transport WebSocket jest zamknięty.");
            return _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
        }

        public async Task CloseAsync(string reason)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason.Length > 100 ? reason.Substring(0, 100) : reason, timeout.Token)
                        .WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException || ex is OperationCanceledException || ex is InvalidOperationException) { }
            finally { _ws.Abort(); }
        }
    }

    /// <summary>Transport TCP: jeden komunikat JSON na linię (używany przez most MetaTrader 5 – gniazda MQL5).</summary>
    public sealed class TcpTransport : IBridgeTransport
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        public TcpTransport(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
            Remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        }

        public string Kind => "TCP";
        public string Remote { get; }

        public async Task SendAsync(string json, CancellationToken ct)
        {
            if (!_client.Connected) return;
            var bytes = Encoding.UTF8.GetBytes(json.Replace("\n", " ") + "\n");
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public Task CloseAsync(string reason)
        {
            try { _client.Close(); } catch (SocketException) { }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Serwer mostów: punkt WebSocket /bridge oraz nasłuch TCP – WYŁĄCZNIE na 127.0.0.1.
    /// Pierwszy komunikat musi być "hello" z kluczem parowania (w ciągu 15 s), inaczej połączenie jest zamykane.
    /// </summary>
    public sealed class BridgeServer
    {
        private readonly AccountManager _accounts;
        private readonly ConfigStore _config;
        private readonly AppLog _log;
        private TcpListener? _tcp;

        public BridgeServer(AccountManager accounts, ConfigStore config, AppLog log)
        {
            _accounts = accounts;
            _config = config;
            _log = log;
        }

        public async Task HandleWebSocketAsync(HttpContext ctx)
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            // Ochrona przed połączeniem ze złośliwej strony WWW w przeglądarce (przeglądarki zawsze wysyłają Origin).
            if (ctx.Request.Headers.ContainsKey("Origin"))
            {
                ctx.Response.StatusCode = 403;
                return;
            }
            var remote = ctx.Connection.RemoteIpAddress;
            if (remote == null || !IPAddress.IsLoopback(remote))
            {
                ctx.Response.StatusCode = 403;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var session = new BridgeSession(new WebSocketTransport(ws, remote.ToString()));
            _accounts.Register(session);
            var buffer = new byte[64 * 1024];
            var ms = new MemoryStream();
            using var helloTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var token = session.State == SessionState.AwaitingHello ? helloTimeout.Token : CancellationToken.None;
                    var res = await ws.ReceiveAsync(buffer, token).ConfigureAwait(false);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, res.Count);
                    if (ms.Length > BridgeProtocol.MaxMessageBytes) { await session.CloseAsync("komunikat zbyt duży"); break; }
                    if (!res.EndOfMessage) continue;
                    string json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    ms.SetLength(0);
                    session.Handle(json);
                    if (session.State == SessionState.Rejected) break;
                }
            }
            catch (OperationCanceledException)
            {
                _log.Warn($"Most {session.Remote}: brak komunikatu hello w ciągu 15 s – rozłączono.");
            }
            catch (WebSocketException ex)
            {
                _log.Warn($"Most {session.Platform} {session.AccountKey}: połączenie przerwane ({ex.Message}).");
            }
            finally
            {
                session.MarkClosed();
                _accounts.Unregister(session);
            }
        }

        public void StartTcp(CancellationToken ct)
        {
            try
            {
                _tcp = new TcpListener(IPAddress.Loopback, _config.Config.BridgeTcpPort);
                _tcp.Start();
            }
            catch (Exception ex) when (ex is SocketException || ex is ArgumentOutOfRangeException)
            {
                // Zajęty port nie może zatrzymać całej aplikacji – panel i most WebSocket (cTrader) działają dalej.
                _log.Error($"Nie można uruchomić serwera mostów TCP na porcie {_config.Config.BridgeTcpPort} ({ex.Message}) – most MetaTrader 5 niedostępny. Zmień BridgeTcpPort w apex.config.json.");
                _tcp = null;
                return;
            }
            _log.Info($"Serwer mostów TCP nasłuchuje na 127.0.0.1:{_config.Config.BridgeTcpPort}");
            var listener = _tcp;
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch (SocketException) { break; }
                    catch (ObjectDisposedException) { break; }
                    _ = Task.Run(() => HandleTcpAsync(client, ct));
                }
            }, ct);
        }

        private async Task HandleTcpAsync(TcpClient client, CancellationToken ct)
        {
            client.NoDelay = true;
            var ep = client.Client.RemoteEndPoint as IPEndPoint;
            if (ep == null || !IPAddress.IsLoopback(ep.Address)) { client.Close(); return; }
            var session = new BridgeSession(new TcpTransport(client));
            _accounts.Register(session);
            try
            {
                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 64 * 1024);
                var helloDeadline = DateTime.UtcNow.AddSeconds(15);
                var sb = new StringBuilder();
                var buf = new char[16 * 1024];
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    if (session.State == SessionState.AwaitingHello)
                    {
                        var left = helloDeadline - DateTime.UtcNow;
                        if (left <= TimeSpan.Zero) { _log.Warn($"Most TCP {session.Remote}: brak hello w ciągu 15 s."); break; }
                        cts.CancelAfter(left);
                    }
                    int n = await reader.ReadAsync(buf.AsMemory(), cts.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        char c = buf[i];
                        if (c == '\n')
                        {
                            string line = sb.ToString().Trim();
                            sb.Clear();
                            if (line.Length > 0) session.Handle(line);
                            if (session.State == SessionState.Rejected) goto done;
                        }
                        else
                        {
                            sb.Append(c);
                            if (sb.Length > BridgeProtocol.MaxMessageBytes) goto done;
                        }
                    }
                }
            done:;
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            finally
            {
                session.MarkClosed();
                _accounts.Unregister(session);
                try { client.Close(); } catch (SocketException) { }
            }
        }

        public void Stop()
        {
            try { _tcp?.Stop(); } catch (SocketException) { }
        }
    }
}
