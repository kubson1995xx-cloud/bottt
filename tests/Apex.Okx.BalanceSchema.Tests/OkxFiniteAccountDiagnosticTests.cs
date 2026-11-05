using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Xunit;

namespace Apex.Tests;

// Actual signed selection + existing exception + production report, no loader,
// filesystem, owner or execution port. Removing any reason or echoing a raw field
// must fail; changing the admission of the exact/reversed profiles must also fail.
public sealed class OkxFiniteAccountDiagnosticTests
{
    public OkxFiniteAccountDiagnosticTests() => _ = new OkxBalanceSchemaDiagnosticTests();
    private const string Code = "FINITE_ACCOUNT_PERMISSION_ROLE_OR_IP_UNSAFE";
    private static readonly string[] AllReasons = { "UID_SCOPE_MISMATCH", "MAIN_UID_SCOPE_MISMATCH", "PERMISSION_NOT_EXACT_READ_AND_TRADE", "ACCOUNT_LEVEL_NOT_1", "ACCOUNT_TYPE_NOT_0", "SPOT_ROLE_TYPE_NOT_0", "AUTO_LOAN_ENABLED", "SPOT_BORROW_ENABLED", "SPOT_BORROW_AUTO_REPAY_ENABLED", "IP_MISSING" };

    public static IEnumerable<object[]> Refusals()
    {
        foreach (string s in new[] { "uid", "mainUid", "reversed", "readOnly", "withdraw", "unknownPermission", "level", "type", "role", "autoLoan", "enableSpotBorrow", "spotBorrowAutoRepay", "emptyIp", "whitespaceIp", "all" })
        {
            string[] reasons = s == "all" ? AllReasons : new[] { s switch {
                "uid" => AllReasons[0], "mainUid" => AllReasons[1],
                "reversed" or "readOnly" or "withdraw" or "unknownPermission" => AllReasons[2],
                "level" => AllReasons[3], "type" => AllReasons[4], "role" => AllReasons[5],
                "autoLoan" => AllReasons[6], "enableSpotBorrow" => AllReasons[7], "spotBorrowAutoRepay" => AllReasons[8], _ => AllReasons[9] } };
            string profile = s switch { "reversed" => "READ_AND_TRADE_REVERSED", "readOnly" => "READ_ONLY", "withdraw" => "READ_TRADE_AND_WITHDRAW", "unknownPermission" or "all" => "UNRECOGNIZED", _ => "READ_AND_TRADE_EXACT" };
            yield return new object[] { s, string.Join(',', reasons), profile };
        }
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Each_reached_predicate_and_combined_refusal_survive_no_owner_report(string scenario, string reasons, string profile)
    {
        using var peer = new Peer(scenario);
        object? selection = null;
        var refusal = await Assert.ThrowsAsync<OkxFiniteAccountRefusal>(() => Observe(peer, d => selection = new { reads = d }));
        Assert.Equal(Code, refusal.Code);
        Assert.Equal(new[] { "/api/v5/account/config" }, peer.Paths);
        Assert.Equal(1, peer.Signatures);
        Assert.NotNull(selection); // finally completed the actual config read
        Assert.True(refusal.Operands != null, "FINITE_AGGREGATE_REFUSAL_LOST_REACHED_PREDICATE_OPERANDS");
        AssertOperands(JsonSerializer.SerializeToElement(refusal.Operands), scenario, reasons, profile);
        // Exact Program catch projection, then unchanged real final serializer.
        selection = new { previous = selection, refusal = refusal.Code, operands = refusal.Operands };
        byte[] bytes = TransactionReport.Build("synthetic-finite-account", DateTimeOffset.UtcNow, true, refusal.Code, new[] { "PROCESS_FINALLY_STOP" }, null, null, null, false, selection);
        try
        {
            using var report = JsonDocument.Parse(bytes); var root = report.RootElement;
            Assert.Equal(Code, root.GetProperty("result").GetString());
            Assert.Equal("STOP", root.GetProperty("mode").GetString());
            Assert.True(root.GetProperty("complete").GetBoolean());
            Assert.False(root.GetProperty("armed").GetBoolean()); Assert.False(root.GetProperty("schedulerStarted").GetBoolean());
            foreach (string n in new[] { "baseline", "orders", "fills", "bills", "protection", "intents", "settledIntents", "obligations", "reservations", "transport", "cash", "quoteCashDelta", "residualBase", "observedBalances", "balanceDeltas" })
                Assert.Equal(JsonValueKind.Null, root.GetProperty(n).ValueKind);
            var s = root.GetProperty("selection"); Assert.Equal(Code, s.GetProperty("refusal").GetString());
            AssertOperands(s.GetProperty("operands"), scenario, reasons, profile);
            string reads = s.GetProperty("previous").GetProperty("reads").GetRawText();
            Assert.Contains("/api/v5/account/config", reads); Assert.Contains("201", reads); // real status, not an assumed 200
            AssertPrivateValuesAbsent(Encoding.UTF8.GetString(bytes));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        Assert.Equal(1, peer.Paths.Count); // no balances/catalog/fees/market/order dispatch
        AssertPrivateValuesAbsent(JsonSerializer.Serialize(refusal.Operands));
    }

    [Fact]
    public async Task Exact_permission_and_nonblank_IP_pass_guard_and_reach_real_next_balance_read()
    {
        using var peer = new Peer("safe"); object? diagnostics = null;
        var refusal = await Assert.ThrowsAsync<OkxFiniteAccountRefusal>(() => Observe(peer, d => diagnostics = d));
        Assert.Equal("FINITE_PREFLIGHT_READ_UNPROVEN", refusal.Code); // deliberate 503 on second GET
        Assert.Null(refusal.Operands); Assert.NotNull(diagnostics);
        Assert.Equal(new[] { "/api/v5/account/config", "/api/v5/account/balance" }, peer.Paths); Assert.Equal(2, peer.Signatures);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("empty")]
    [InlineData("multiple")]
    public async Task Unproven_or_non_singleton_read_does_not_claim_account_predicates(string scenario)
    {
        using var peer = new Peer(scenario);
        var refusal = await Assert.ThrowsAsync<OkxFiniteAccountRefusal>(() => Observe(peer, _ => { }));
        Assert.Equal("FINITE_PREFLIGHT_READ_UNPROVEN", refusal.Code); Assert.Null(refusal.Operands);
        Assert.Single(peer.Paths); Assert.Equal(1, peer.Signatures);
    }

    [Fact]
    public async Task Kernel_IPv4_IPv6_denial_is_calibrated() => await new OkxBalanceSchemaDiagnosticTests().Linux_network_denial_is_calibrated_on_async_workers();

    private static void AssertOperands(JsonElement o, string scenario, string reasons, string profile)
    {
        Assert.Equal(new[] { "AccountLevelIs1", "AccountTypeIs0", "AutoLoan", "EnableSpotBorrow", "IpMissing", "MainUidMatchesScope", "PermissionProfile", "Reasons", "SpotBorrowAutoRepay", "SpotRoleTypeIs0", "UidMatchesScope" }, o.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(reasons.Split(','), o.GetProperty("Reasons").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(profile, o.GetProperty("PermissionProfile").GetString());
        foreach (var p in new[] { ("UidMatchesScope", "uid"), ("MainUidMatchesScope", "mainUid"), ("AccountLevelIs1", "level"), ("AccountTypeIs0", "type"), ("SpotRoleTypeIs0", "role") })
            Assert.Equal(scenario != p.Item2 && scenario != "all", o.GetProperty(p.Item1).GetBoolean());
        foreach (var p in new[] { ("AutoLoan", "autoLoan"), ("EnableSpotBorrow", "enableSpotBorrow"), ("SpotBorrowAutoRepay", "spotBorrowAutoRepay") })
            Assert.Equal(scenario == p.Item2 || scenario == "all", o.GetProperty(p.Item1).GetBoolean());
        Assert.Equal(scenario is "emptyIp" or "whitespaceIp" or "all", o.GetProperty("IpMissing").GetBoolean());
    }
    private static void AssertPrivateValuesAbsent(string output)
    {
        foreach (string s in new[] { Peer.Key, Peer.Secret, Peer.Pass, Peer.Uid, Peer.OtherUid, Peer.Private, Peer.Ip, "read_only,trade", "trade,read_only", "read_only,trade,withdraw", Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Peer.Key))).ToLowerInvariant() })
            Assert.True(!output.Contains(s, StringComparison.Ordinal), "PRIVATE_VALUE_OUTPUT; values withheld");
    }
    private static async Task<OkxFiniteNomination> Observe(Peer peer, Action<object> diagnostics)
    {
        var scope = new OkxExecutionScope("Demo", Peer.Uid, Peer.Uid, "USDC", "synthetic-finite-account");
        using var credentials = new EphemeralCredentials(Encoding.ASCII.GetBytes(Peer.Key), Encoding.ASCII.GetBytes(Peer.Secret), Encoding.ASCII.GetBytes(Peer.Pass));
        return await OkxFiniteAccountSelection.ObserveAsync(scope, credentials.ForObservation(scope), credentials.Fingerprint, () => true, default, diagnostics, TimeProvider.System,
            (s,k,h,f) => OkxExecutionObservationClient.ForTests(peer, TimeProvider.System, s,k,h,f));
    }
    private sealed class Peer(string scenario) : HttpMessageHandler
    {
        internal const string Key = "synthetic-finite-key-never-output", Secret = "synthetic-finite-secret-never-output", Pass = "synthetic-finite-pass-never-output";
        internal const string Uid = "973456789012345678", OtherUid = "873456789012345678", Private = "ARBITRARY_FINITE_ACCOUNT_TEXT_NEVER_OUTPUT", Ip = "192.0.2.198";
        internal readonly List<string> Paths = new(); internal int Signatures;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, r.Method); Assert.Null(r.Content);
            Assert.Equal("https", r.RequestUri!.Scheme); Assert.Equal("eea.okx.com", r.RequestUri.Host); Assert.Equal(443, r.RequestUri.Port);
            Assert.Equal("1", r.Headers.GetValues("x-simulated-trading").Single());
            string path = r.RequestUri.PathAndQuery; Paths.Add(path);
            Assert.Equal(Paths.Count == 1 ? "/api/v5/account/config" : "/api/v5/account/balance", path); Assert.InRange(Paths.Count, 1, 2);
            Assert.True(r.Headers.GetValues("OK-ACCESS-KEY").Single() == Key, "SYNTHETIC_KEY_MISMATCH; withheld");
            Assert.True(r.Headers.GetValues("OK-ACCESS-PASSPHRASE").Single() == Pass, "SYNTHETIC_PASS_MISMATCH; withheld");
            string stamp = r.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
            string sign = Convert.ToBase64String(HMACSHA256.HashData(Encoding.ASCII.GetBytes(Secret), Encoding.UTF8.GetBytes(stamp + "GET" + path)));
            Assert.True(r.Headers.GetValues("OK-ACCESS-SIGN").Single() == sign, "SYNTHETIC_SIGNATURE_MISMATCH; withheld"); Signatures++;
            var a = new JsonObject { ["uid"] = Uid, ["mainUid"] = Uid, ["acctLv"] = "1", ["perm"] = "read_only,trade", ["ip"] = Ip, ["feeType"] = "0", ["autoLoan"] = false, ["enableSpotBorrow"] = false, ["spotBorrowAutoRepay"] = false, ["type"] = "0", ["spotRoleType"] = "0", ["kycLv"] = "3", ["label"] = Private };
            if (scenario is "uid" or "all") a["uid"] = OtherUid;
            if (scenario is "mainUid" or "all") a["mainUid"] = OtherUid;
            if (scenario == "reversed") a["perm"] = "trade,read_only";
            if (scenario == "readOnly") a["perm"] = "read_only";
            if (scenario == "withdraw") a["perm"] = "read_only,trade,withdraw";
            if (scenario is "unknownPermission" or "all") a["perm"] = Private;
            foreach (var p in new[] { ("level", "acctLv"), ("type", "type"), ("role", "spotRoleType") }) if (scenario == p.Item1 || scenario == "all") a[p.Item2] = Private;
            foreach (string n in new[] { "autoLoan", "enableSpotBorrow", "spotBorrowAutoRepay" }) if (scenario == n || scenario == "all") a[n] = true;
            if (scenario == "emptyIp") a["ip"] = "";
            if (scenario is "whitespaceIp" or "all") a["ip"] = " \t\r\n";
            var rows = new JsonArray(a); if (scenario == "empty") rows.Clear(); if (scenario == "multiple") rows.Add(a.DeepClone());
            string body = new JsonObject { ["code"] = "0", ["msg"] = Private, ["data"] = rows }.ToJsonString();
            return Task.FromResult(new HttpResponseMessage(Paths.Count == 2 || scenario == "http" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created) { Content = new StringContent(body) });
        }
    }
}
