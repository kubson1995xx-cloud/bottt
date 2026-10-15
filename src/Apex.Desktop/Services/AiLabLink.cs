using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Apex.Desktop.Services
{
    /// <summary>
    /// Łącznik z modułem APEX AI Lab (osobny proces). APEX Desktop jedynie przekazuje zapytania panelu do AI Lab
    /// i na żądanie uruchamia jego proces. Silnik handlowy nie jest w żaden sposób powiązany z AI Lab.
    /// </summary>
    public sealed class AiLabLink
    {
        private readonly string _dataDir;
        private readonly AppLog _log;
        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

        public AiLabLink(string dataDir, AppLog log)
        {
            _dataDir = dataDir;
            _log = log;
        }

        private string LabDir => Path.Combine(_dataDir, "ailab");

        private (int port, string token)? Endpoint()
        {
            string p = Path.Combine(LabDir, "ailab.config.json");
            if (!File.Exists(p)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                int port = doc.RootElement.TryGetProperty("HttpPort", out var hp) && hp.TryGetInt32(out int v) ? v : 5079;
                string token = doc.RootElement.TryGetProperty("ApiToken", out var t) ? t.GetString() ?? "" : "";
                return token.Length >= 32 ? (port, token) : null;
            }
            catch (JsonException) { return null; }
            catch (IOException) { return null; }
        }

        /// <summary>Lokalizacja programu AI Lab: zapisana przez AI Lab, obok APEX Desktop w pakiecie lub z zmiennej APEX_AILAB_EXE.</summary>
        public string? FindExecutable()
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("APEX_AILAB_EXE"),
                ReadPathFile(),
                Path.Combine(AppContext.BaseDirectory, "..", "APEX-AiLab", "ApexAiLab.exe"),
                Path.Combine(AppContext.BaseDirectory, "ApexAiLab.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "APEX-AiLab", "ApexAiLab"),
            };
            foreach (var c in candidates)
                if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return Path.GetFullPath(c);
            return null;
        }

        private string? ReadPathFile()
        {
            try
            {
                string p = Path.Combine(LabDir, "ailab.path");
                return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
            }
            catch (IOException) { return null; }
        }

        public async Task<object> StatusAsync()
        {
            string? exe = FindExecutable();
            bool running = false;
            string version = "";
            var ep = Endpoint();
            if (ep != null)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var r = await Http.GetAsync($"http://127.0.0.1:{ep.Value.port}/api/health", cts.Token).ConfigureAwait(false);
                    if (r.IsSuccessStatusCode)
                    {
                        running = true;
                        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false));
                        version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                    }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
            }
            return new { installed = exe != null, exePath = exe, running, version, autostart = File.Exists(Path.Combine(LabDir, "autostart")) };
        }

        public string Start()
        {
            string? exe = FindExecutable();
            if (exe == null) return "Nie znaleziono programu APEX AI Lab (ApexAiLab.exe). Zbuduj pakiet skryptem Zbuduj-APEX.ps1.";
            try
            {
                var psi = exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? new ProcessStartInfo("dotnet") : new ProcessStartInfo(exe);
                if (exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(exe);
                psi.ArgumentList.Add("--data");
                psi.ArgumentList.Add(_dataDir);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetDirectoryName(exe)!;
                Process.Start(psi);
                _log.Info("Uruchomiono moduł APEX AI Lab: " + exe);
                return "Uruchamianie modułu AI Lab…";
            }
            catch (Win32Exception ex)
            {
                return "Nie udało się uruchomić AI Lab: " + ex.Message;
            }
        }

        /// <summary>Autostart AI Lab razem z APEX (jeśli użytkownik włączył tę opcję w panelu).</summary>
        public async Task AutostartAsync()
        {
            if (!File.Exists(Path.Combine(LabDir, "autostart"))) return;
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var ep = Endpoint();
            if (ep != null)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var r = await Http.GetAsync($"http://127.0.0.1:{ep.Value.port}/api/health", cts.Token).ConfigureAwait(false);
                    if (r.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
            }
            Start();
        }

        /// <summary>Przekazanie zapytania panelu do AI Lab (/api/ai/... → AI Lab /api/...).</summary>
        public async Task ProxyAsync(HttpContext ctx, string path)
        {
            var ep = Endpoint();
            if (ep == null) { await NotRunning(ctx); return; }
            if (path.Contains("..", StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal)) { ctx.Response.StatusCode = 400; return; }
            string url = $"http://127.0.0.1:{ep.Value.port}/api/{path}{ctx.Request.QueryString}";
            using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), url);
            req.Headers.Add("X-Lab-Token", ep.Value.token);
            if (HttpMethods.IsPost(ctx.Request.Method))
            {
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                string body = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (body.Length > 1_000_000) { ctx.Response.StatusCode = 413; return; }
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            // Zatwierdzenie zmiany buduje wydanie (kilka minut) – dłuższy limit czasu.
            var timeout = path.EndsWith("/approve", StringComparison.Ordinal) ? TimeSpan.FromMinutes(45) : TimeSpan.FromSeconds(60);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            cts.CancelAfter(timeout);
            try
            {
                using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
                ctx.Response.StatusCode = (int)resp.StatusCode;
                ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8";
                await resp.Content.CopyToAsync(ctx.Response.Body).ConfigureAwait(false);
            }
            catch (HttpRequestException) { await NotRunning(ctx); }
            catch (TaskCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                ctx.Response.StatusCode = 504;
                await ctx.Response.WriteAsJsonAsync(new { message = "AI Lab nie odpowiedział w czasie." }).ConfigureAwait(false);
            }
        }

        private static Task NotRunning(HttpContext ctx)
        {
            ctx.Response.StatusCode = 503;
            return ctx.Response.WriteAsJsonAsync(new { message = "Moduł AI Lab nie działa.", notRunning = true });
        }
    }
}
