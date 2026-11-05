using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Okx.DemoAcceptance;
using Xunit;

namespace Apex.Tests;

public sealed class OkxEnrollmentAccountSafetyTests
{
    // Catches loss of any reached predicate, fabricated/unsanitized profile values,
    // later private reads after refusal, and null-Observation report data loss.
    [Theory]
    [InlineData("permission", "PERMISSION_NOT_ALLOWED", "READ_AND_WITHDRAW", "1", false, false, false)]
    [InlineData("level2", "ACCOUNT_LEVEL_NOT_1", "READ_AND_TRADE", "2", false, false, false)]
    [InlineData("level3", "ACCOUNT_LEVEL_NOT_1", "READ_AND_TRADE", "3", false, false, false)]
    [InlineData("level4", "ACCOUNT_LEVEL_NOT_1", "READ_AND_TRADE", "4", false, false, false)]
    [InlineData("autoLoan", "AUTO_LOAN_ENABLED", "READ_AND_TRADE", "1", true, false, false)]
    [InlineData("enableSpotBorrow", "SPOT_BORROW_ENABLED", "READ_AND_TRADE", "1", false, true, false)]
    [InlineData("spotBorrowAutoRepay", "SPOT_BORROW_AUTO_REPAY_ENABLED", "READ_AND_TRADE", "1", false, false, true)]
    [InlineData("unknownPermission", "PERMISSION_NOT_ALLOWED", "UNAVAILABLE", "1", false, false, false)]
    [InlineData("unknownLevel", "ACCOUNT_LEVEL_NOT_1", "READ_AND_TRADE", "UNAVAILABLE", false, false, false)]
    [InlineData("all", "PERMISSION_NOT_ALLOWED,ACCOUNT_LEVEL_NOT_1,AUTO_LOAN_ENABLED,SPOT_BORROW_ENABLED,SPOT_BORROW_AUTO_REPAY_ENABLED", "UNAVAILABLE", "UNAVAILABLE", true, true, true)]
    public async Task Unsafe_account_keeps_precise_safe_facts_in_null_observation_finite_report(string scenario, string reasons, string permissions, string level, bool autoLoan, bool spotBorrow, bool autoRepay)
    {
        var peer = new AccountPeer(scenario);
        var discovery = await Discover(peer);
        Assert.Equal("DISCOVERY_UNSAFE_ACCOUNT", discovery.Code);
        Assert.Null(discovery.Observation);
        Assert.Equal(new[] { "/api/v5/public/time", "/api/v5/account/config" }, peer.Paths);
        Assert.NotNull(discovery.Diagnostic); // Original production branch loses the facts here.
        using var diagnostic = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(discovery.Diagnostic));
        AssertDiagnostic(diagnostic.RootElement, reasons, permissions, level, autoLoan, spotBorrow, autoRepay, 200);
        AssertPrivateValuesAbsent(JsonSerializer.Serialize(discovery));

        // Exactly the refusal projection used by Program.Main, then the actual
        // production TransactionReport DLL, with neither Observation nor owner.
        object projection = new { discovery.Code, discovery.Diagnostic };
        byte[] bytes = TransactionReport.Build("synthetic-account-safety", DateTimeOffset.UtcNow, true, "DISCOVERY_REFUSED",
            new[] { "DISCOVERY_REFUSED", "PROCESS_FINALLY_STOP" }, null, projection, null, false);
        try
        {
            using var report = JsonDocument.Parse(bytes);
            var root = report.RootElement;
            var refusal = root.GetProperty("discoveryDiagnostic");
            Assert.Equal("DISCOVERY_UNSAFE_ACCOUNT", refusal.GetProperty("Code").GetString());
            AssertDiagnostic(refusal.GetProperty("Diagnostic"), reasons, permissions, level, autoLoan, spotBorrow, autoRepay, 200);
            Assert.Equal("STOP", root.GetProperty("mode").GetString());
            Assert.False(root.GetProperty("armed").GetBoolean());
            Assert.False(root.GetProperty("schedulerStarted").GetBoolean());
            Assert.True(root.GetProperty("complete").GetBoolean());
            foreach (string name in new[] { "baseline", "orders", "fills", "intents", "reservations", "transport" })
                Assert.Equal(JsonValueKind.Null, root.GetProperty(name).ValueKind);
            AssertPrivateValuesAbsent(Encoding.UTF8.GetString(bytes));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Fact]
    public async Task Finite_report_retains_reached_refusal_details_without_an_observation_or_financial_owner()
    {
        var peer = new AccountPeer("all");
        var discovery = await Discover(peer);
        Assert.Equal("DISCOVERY_UNSAFE_ACCOUNT", discovery.Code);
        Assert.Null(discovery.Observation);
        Assert.Equal(2, peer.Paths.Count);
        byte[] bytes = TransactionReport.Build("synthetic-report-refusal", DateTimeOffset.UtcNow, true, "DISCOVERY_REFUSED",
            Array.Empty<string>(), null, new { discovery.Code, discovery.Diagnostic }, null, false);
        try
        {
            using var report = JsonDocument.Parse(bytes);
            var diagnostic = report.RootElement.GetProperty("discoveryDiagnostic").GetProperty("Diagnostic");
            Assert.Equal(JsonValueKind.Object, diagnostic.ValueKind); // Reached original missing-report assertion.
            AssertDiagnostic(diagnostic, "PERMISSION_NOT_ALLOWED,ACCOUNT_LEVEL_NOT_1,AUTO_LOAN_ENABLED,SPOT_BORROW_ENABLED,SPOT_BORROW_AUTO_REPAY_ENABLED",
                "UNAVAILABLE", "UNAVAILABLE", true, true, true, 200);
            AssertPrivateValuesAbsent(Encoding.UTF8.GetString(bytes));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Theory]
    [InlineData("read_only")]
    [InlineData("read_only,trade")]
    [InlineData("trade,read_only")]
    public async Task Existing_safe_profiles_remain_read_only_stop_without_unsafe_diagnostics(string permissions)
    {
        var peer = new AccountPeer("safe", permissions: permissions);
        var discovery = await Discover(peer);
        Assert.Equal("CONNECTED_READ_ONLY_STOP", discovery.Code);
        Assert.NotNull(discovery.Observation);
        Assert.Equal(4, peer.Paths.Count);
        Assert.Null(discovery.Diagnostic);
    }

    [Fact]
    public async Task Account_safety_provenance_keeps_actual_success_status_not_an_assumed_http_200()
    {
        var peer = new AccountPeer("autoLoan", status: 201);
        var discovery = await Discover(peer);
        Assert.Equal("DISCOVERY_UNSAFE_ACCOUNT", discovery.Code);
        Assert.NotNull(discovery.Diagnostic);
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(discovery.Diagnostic));
        AssertDiagnostic(json.RootElement, "AUTO_LOAN_ENABLED", "READ_AND_TRADE", "1", true, false, false, 201);
        Assert.Equal(2, peer.Paths.Count);
    }

    [Theory]
    [InlineData("identity", "DISCOVERY_IDENTITY_MISMATCH")]
    [InlineData("malformed", "DISCOVERY_INVALID_PAYLOAD")]
    [InlineData("venue", "DISCOVERY_API_50101 [GET /api/v5/account/config; HTTP 200; OKX 50101]")]
    [InlineData("http", "DISCOVERY_HTTP_REFUSED [GET /api/v5/account/config; HTTP 401; OKX 50111]")]
    public async Task Earlier_identity_parser_and_http_refusals_do_not_claim_reached_account_safety(string scenario, string code)
    {
        var peer = new AccountPeer(scenario);
        var discovery = await Discover(peer);
        Assert.Equal(code, discovery.Code);
        Assert.Null(discovery.Observation);
        Assert.Equal(2, peer.Paths.Count);
        if (discovery.Diagnostic != null)
        {
            using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(discovery.Diagnostic));
            Assert.False(json.RootElement.TryGetProperty("AccountSafety", out var safety) && safety.ValueKind != JsonValueKind.Null);
        }
        AssertPrivateValuesAbsent(JsonSerializer.Serialize(discovery));
    }

    private static Task<OkxEnrollmentDiscovery> Discover(AccountPeer peer) => OkxEnrollmentDiscoveryClient.RunAsync("Demo", "DemoTradingApi",
        Encoding.ASCII.GetBytes(AccountPeer.Key), Encoding.ASCII.GetBytes(AccountPeer.Secret), Encoding.ASCII.GetBytes(AccountPeer.Pass), null, null, default, peer);

    private static void AssertDiagnostic(JsonElement d, string reasons, string permissions, string level, bool autoLoan, bool spotBorrow, bool autoRepay, int http)
    {
        Assert.Equal("/api/v5/account/config", d.GetProperty("Endpoint").GetString());
        Assert.Equal(http, d.GetProperty("HttpStatus").GetInt32());
        Assert.Equal("0", d.GetProperty("OkxCode").GetString());
        var s = d.GetProperty("AccountSafety");
        Assert.Equal(reasons.Split(','), s.GetProperty("Reasons").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(permissions, s.GetProperty("PermissionProfile").GetString());
        Assert.Equal(level, s.GetProperty("AccountLevel").GetString());
        Assert.Equal(autoLoan, s.GetProperty("AutoLoan").GetBoolean());
        Assert.Equal(spotBorrow, s.GetProperty("EnableSpotBorrow").GetBoolean());
        Assert.Equal(autoRepay, s.GetProperty("SpotBorrowAutoRepay").GetBoolean());
        Assert.Equal(new[] { "AccountLevel", "AutoLoan", "EnableSpotBorrow", "PermissionProfile", "Reasons", "SpotBorrowAutoRepay" }, s.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    private static void AssertPrivateValuesAbsent(string output)
    {
        foreach (string value in new[] { AccountPeer.Key, AccountPeer.Secret, AccountPeer.Pass, AccountPeer.Uid, AccountPeer.OtherUid,
            AccountPeer.PrivateText, AccountPeer.Ip, Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(AccountPeer.Key))).ToLowerInvariant() })
            Assert.True(!output.Contains(value, StringComparison.Ordinal), "PRIVATE_SENTINEL_LEAK; values withheld");
    }

    // Only the wire is replaced. Production signing, strict parsing, UID guard,
    // safety guard and report serializer execute unchanged. No private network.
    private sealed class AccountPeer(string scenario, string permissions = "read_only,trade", int status = 200) : HttpMessageHandler
    {
        internal const string Key = "synthetic-account-key-DO-NOT-PUBLISH";
        internal const string Secret = "synthetic-account-secret-DO-NOT-PUBLISH";
        internal const string Pass = "synthetic-account-pass-DO-NOT-PUBLISH";
        internal const string Uid = "912345678901234567";
        internal const string OtherUid = "812345678901234567";
        internal const string PrivateText = "UNTRUSTED_ACCOUNT_TEXT_DO_NOT_PUBLISH";
        internal const string Ip = "192.0.2.197";
        internal readonly List<string> Paths = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, r.Method); Assert.Null(r.Content);
            Assert.Equal("https", r.RequestUri!.Scheme); Assert.Equal("eea.okx.com", r.RequestUri.Host); Assert.Equal(443, r.RequestUri.Port);
            Assert.Equal("1", r.Headers.GetValues("x-simulated-trading").Single());
            string path = r.RequestUri.PathAndQuery; Paths.Add(path);
            Assert.Equal(new[] { "/api/v5/public/time", "/api/v5/account/config", "/api/v5/account/balance", "/api/v5/account/instruments?instType=SPOT" }[Paths.Count - 1], path);
            string body;
            if (Paths.Count == 1)
            {
                Assert.False(r.Headers.Any(h => h.Key.StartsWith("OK-ACCESS-", StringComparison.OrdinalIgnoreCase)));
                body = "{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"ts\":\"" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "\"}]}";
            }
            else
            {
                Assert.True(r.Headers.GetValues("OK-ACCESS-KEY").Single() == Key, "Synthetic key mismatch; withheld");
                Assert.True(r.Headers.GetValues("OK-ACCESS-PASSPHRASE").Single() == Pass, "Synthetic passphrase mismatch; withheld");
                string stamp = r.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
                string signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.ASCII.GetBytes(Secret), Encoding.UTF8.GetBytes(stamp + "GET" + path)));
                Assert.True(r.Headers.GetValues("OK-ACCESS-SIGN").Single() == signature, "Synthetic signature mismatch; withheld");
                if (Paths.Count == 2)
                {
                    // Populated account shape from the retained independent EEA R
                    // control (baseline-schema.json R:2806-2934), not parser output.
                    var a = JsonNode.Parse("""
                        {"acctLv":"1","acctStpMode":"cancel_maker","autoLoan":false,"ctIsoMode":"automatic","enableSpotBorrow":false,"greeksType":"PA","feeType":"0","ip":"","type":"0","kycLv":"3","label":"","level":"Lv1","levelTmp":"","liquidationGear":"-1","mainUid":"1","mgnIsoMode":"automatic","opAuth":"1","perm":"read_only,trade","posMode":"long_short_mode","roleType":"0","spotBorrowAutoRepay":false,"spotOffsetType":"","spotRoleType":"0","spotTraderInsts":[],"stgyType":"0","traderInsts":[],"uid":"1","settleCcy":"USDC","settleCcyList":["USD","USDC","USDG"]}
                        """)!;
                    a["uid"] = Uid; a["mainUid"] = scenario == "identity" ? OtherUid : Uid;
                    a["ip"] = Ip; a["label"] = PrivateText; a["perm"] = permissions;
                    if (scenario == "permission") a["perm"] = "read_only,withdraw";
                    if (scenario.StartsWith("level", StringComparison.Ordinal)) a["acctLv"] = scenario.Substring(5);
                    if (scenario is "unknownPermission" or "all") a["perm"] = PrivateText;
                    if (scenario is "unknownLevel" or "all") a["acctLv"] = PrivateText;
                    foreach (string flag in new[] { "autoLoan", "enableSpotBorrow", "spotBorrowAutoRepay" })
                        if (scenario == flag || scenario is "all" or "identity") a[flag] = true;
                    if (scenario == "malformed") a["autoLoan"] = "true";
                    body = new JsonObject { ["code"] = scenario == "venue" ? "50101" : "0", ["msg"] = PrivateText, ["data"] = new JsonArray(a) }.ToJsonString();
                    if (scenario == "http") body = "{\"code\":\"50111\",\"msg\":\"" + PrivateText + "\",\"data\":[]}";
                }
                else if (Paths.Count == 3) body = "{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"uTime\":\"" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "\",\"details\":[]}]}";
                else body = "{\"code\":\"0\",\"msg\":\"\",\"data\":[]}";
            }
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)(Paths.Count == 2 ? scenario == "http" ? 401 : status : 200)) { Content = new StringContent(body) });
        }
    }
}
