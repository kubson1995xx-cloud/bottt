using System;
using System.Collections.Generic;
using System.IO;
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

public sealed class OkxEnrollmentPayloadDiagnosticTests
{
    // Original defect: every one of these distinct reached boundaries returns
    // DISCOVERY_INVALID_PAYLOAD/null. Tests use the real signer/parser/reporter.
    [Theory]
    [InlineData("time-type", 1, "PARSE_SCHEMA", "TYPE", "$.data[].ts", "String", "Number", "NONE")]
    [InlineData("time-stamp", 1, "PARSE_VALUES", "TIMESTAMP", "$.data[].ts", "UnixMilliseconds", "String", "NONE")]
    [InlineData("config-type", 2, "PARSE_SCHEMA", "TYPE", "$.data[].autoLoan", "Boolean", "String", "TIME_VALIDATED")]
    [InlineData("config-missing", 2, "PARSE_VALUES", "MISSING", "$.data[].uid", "String", "Missing", "TIME_VALIDATED")]
    [InlineData("config-unknown", 2, "PARSE_SCHEMA", "UNSUPPORTED_PROPERTY", "$.data[].<unknown>", "KnownProperty", "String", "TIME_VALIDATED")]
    [InlineData("config-case", 2, "PARSE_SCHEMA", "PROPERTY_CASE", "$.data[].uid", "ExactProperty", "String", "TIME_VALIDATED")]
    [InlineData("config-duplicate", 2, "PARSE_UNIQUE", "DUPLICATE_PROPERTY", "$.data[].uid", "UniqueProperty", "String", "TIME_VALIDATED")]
    [InlineData("config-string", 2, "PARSE_UNIQUE", "INVALID_STRING", "$.data[].label", "UnicodeString", "String", "TIME_VALIDATED")]
    [InlineData("balance-type", 3, "PARSE_SCHEMA", "TYPE", "$.data[].details[].cashBal", "String", "Number", "ACCOUNT_SAFETY_VALIDATED")]
    [InlineData("balance-number", 3, "PARSE_VALUES", "NUMBER", "$.data[].details[].cashBal", "ExactDecimal", "String", "ACCOUNT_SAFETY_VALIDATED")]
    [InlineData("balance-precision", 3, "PARSE_VALUES", "NUMBER_PRECISION", "$.data[].details[].cashBal", "ExactDecimal", "String", "ACCOUNT_SAFETY_VALIDATED")]
    [InlineData("balance-stamp", 3, "PARSE_VALUES", "TIMESTAMP", "$.data[].uTime", "UnixMilliseconds", "String", "ACCOUNT_SAFETY_VALIDATED")]
    [InlineData("balance-cardinality", 3, "PARSE_ENVELOPE", "CARDINALITY", "$.data", "ExactlyOne", "Empty", "ACCOUNT_SAFETY_VALIDATED")]
    [InlineData("balance-details", 3, "PARSE_VALUES", "MISSING", "$.data[].details", "Array", "Missing", "ACCOUNT_SAFETY_VALIDATED")]
    [InlineData("balance-duplicate", 3, "PARSE_UNIQUE", "DUPLICATE_PROPERTY", "$.data[].details[].cashBal", "UniqueProperty", "String", "ACCOUNT_SAFETY_VALIDATED")]
    [InlineData("instrument-type", 4, "PARSE_SCHEMA", "TYPE", "$.data[].instIdCode", "IntegerOrNull", "String", "BALANCE_VALIDATED")]
    [InlineData("instrument-missing", 4, "PARSE_VALUES", "MISSING", "$.data[].tradeQuoteCcyList", "Array", "Missing", "BALANCE_VALIDATED")]
    [InlineData("instrument-array", 4, "PARSE_SCHEMA", "TYPE", "$.data[].tradeQuoteCcyList[]", "String", "Object", "BALANCE_VALIDATED")]
    [InlineData("instrument-nested", 4, "PARSE_SCHEMA", "TYPE", "$.data[].upcChg[].effTime", "String", "Number", "BALANCE_VALIDATED")]
    [InlineData("instrument-number", 4, "PARSE_VALUES", "NUMBER", "$.data[].lotSz", "ExactDecimal", "String", "BALANCE_VALIDATED")]
    [InlineData("instrument-utf8", 4, "PARSE_UTF8", "INVALID_UTF8", "$", "Utf8", "Invalid", "BALANCE_VALIDATED")]
    [InlineData("instrument-json", 4, "PARSE_JSON", "INVALID_JSON", "$", "JsonDocument", "Invalid", "BALANCE_VALIDATED")]
    public async Task Invalid_payload_retains_exact_closed_boundary_in_null_owner_production_report(string scenario, int reached, string phase, string reason, string path, string expected, string actual, string stage)
    {
        var peer = new Peer(scenario);
        var discovery = await Discover(peer);
        Assert.Equal("DISCOVERY_INVALID_PAYLOAD", discovery.Code);
        Assert.Null(discovery.Observation);
        Assert.Equal(reached, peer.Paths.Count);
        Assert.NotNull(discovery.Diagnostic); // Reached RED on original product.
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(discovery.Diagnostic));
        AssertBoundary(doc.RootElement, reached, phase, reason, path, expected, actual, stage);
        AssertPrivateAbsent(JsonSerializer.Serialize(discovery));
        byte[] bytes = TransactionReport.Build("synthetic-payload", DateTimeOffset.UtcNow, true, "DISCOVERY_REFUSED", new[] { "PROCESS_FINALLY_STOP" }, null, new { discovery.Code, discovery.Diagnostic }, null, false);
        try
        {
            using var report = JsonDocument.Parse(bytes);
            var root = report.RootElement;
            AssertBoundary(root.GetProperty("discoveryDiagnostic").GetProperty("Diagnostic"), reached, phase, reason, path, expected, actual, stage);
            Assert.Equal("STOP", root.GetProperty("mode").GetString());
            Assert.False(root.GetProperty("armed").GetBoolean());
            Assert.False(root.GetProperty("schedulerStarted").GetBoolean());
            foreach (string name in new[] { "baseline", "orders", "fills", "intents", "reservations", "transport" }) Assert.Equal(JsonValueKind.Null, root.GetProperty(name).ValueKind);
            AssertPrivateAbsent(Encoding.UTF8.GetString(bytes));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Theory]
    [InlineData("time-semantic", "DISCOVERY_CLOCK_REFUSED", 1, "SEMANTIC_TIME", "TIMESTAMP_UNAVAILABLE", "$.data[].ts", "NONE")]
    [InlineData("config-semantic", "DISCOVERY_IDENTITY_MISMATCH", 2, "SEMANTIC_IDENTITY", "IDENTITY", "$.data[]", "CONFIG_PARSED_AUTHENTICATED")]
    [InlineData("balance-semantic", "DISCOVERY_BALANCE_INVALID", 3, "SEMANTIC_BALANCE", "CURRENCY_GRAMMAR", "$.data[].details[].ccy", "ACCOUNT_SAFETY_VALIDATED")]
    [InlineData("balance-currencies", "DISCOVERY_BALANCE_INVALID", 3, "SEMANTIC_BALANCE", "DUPLICATE_CURRENCY", "$.data[].details[].ccy", "ACCOUNT_SAFETY_VALIDATED")]
    [InlineData("instrument-semantic", "DISCOVERY_INSTRUMENT_INVALID", 4, "SEMANTIC_INSTRUMENTS", "INSTRUMENT_GRAMMAR", "$.data[].instId", "BALANCE_VALIDATED")]
    [InlineData("instrument-identities", "DISCOVERY_INSTRUMENT_INVALID", 4, "SEMANTIC_INSTRUMENTS", "DUPLICATE_INSTRUMENT", "$.data[].instId", "BALANCE_VALIDATED")]
    public async Task Postparse_semantics_keep_the_exact_source_phase_without_values(string scenario, string code, int reached, string phase, string reason, string path, string stage)
    {
        var peer = new Peer(scenario); var result = await Discover(peer);
        Assert.Equal(code, result.Code); Assert.Null(result.Observation); Assert.Equal(reached, peer.Paths.Count);
        Assert.NotNull(result.Diagnostic);
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(result.Diagnostic)); var d = doc.RootElement;
        Assert.Equal(phase, d.GetProperty("Phase").GetString()); Assert.Equal(stage, d.GetProperty("LastCompletedStage").GetString());
        Assert.Equal(reason, d.GetProperty("Payload").GetProperty("Reason").GetString()); Assert.Equal(path, d.GetProperty("Payload").GetProperty("Path").GetString());
        Assert.Equal("0", d.GetProperty("OkxCode").GetString()); AssertPrivateAbsent(JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("official")]
    [InlineData("optional-absent")]
    public async Task Independently_retained_official_controls_and_optional_metadata_parse_without_contract_relaxation(string scenario)
    {
        var peer = new Peer(scenario); var result = await Discover(peer);
        Assert.Equal("CONNECTED_READ_ONLY_STOP", result.Code); Assert.NotNull(result.Observation); Assert.Equal(4, peer.Paths.Count); Assert.Null(result.Diagnostic);
    }

    [Fact]
    public async Task Transport_before_response_keeps_endpoint_but_never_invents_http_or_okx_success()
    {
        var peer = new Peer("transport"); var result = await Discover(peer);
        Assert.Equal("DISCOVERY_TRANSPORT_REFUSED", result.Code); Assert.Null(result.Observation); Assert.Equal(3, peer.Paths.Count); Assert.NotNull(result.Diagnostic);
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(result.Diagnostic)); var d = doc.RootElement;
        Assert.Equal(Peer.Targets[2], d.GetProperty("Endpoint").GetString()); Assert.Equal(JsonValueKind.Null, d.GetProperty("HttpStatus").ValueKind); Assert.Equal(JsonValueKind.Null, d.GetProperty("OkxCode").ValueKind);
        Assert.Equal("REQUEST", d.GetProperty("Phase").GetString()); Assert.Equal("ACCOUNT_SAFETY_VALIDATED", d.GetProperty("LastCompletedStage").GetString()); AssertPrivateAbsent(JsonSerializer.Serialize(result));
    }

    private static void AssertBoundary(JsonElement d, int reached, string phase, string reason, string path, string expected, string actual, string stage)
    {
        Assert.Equal(Peer.Targets[reached - 1], d.GetProperty("Endpoint").GetString());
        Assert.Equal(201, d.GetProperty("HttpStatus").GetInt32()); // Non-200 success must not be fabricated as 200.
        Assert.Equal(phase, d.GetProperty("Phase").GetString()); Assert.Equal(stage, d.GetProperty("LastCompletedStage").GetString());
        Assert.Equal(reached > 2, d.GetProperty("ConfigAuthenticated").GetBoolean()); Assert.Equal(reached > 2, d.GetProperty("AccountSafetyValidated").GetBoolean());
        var f = d.GetProperty("Payload"); Assert.Equal(reason, f.GetProperty("Reason").GetString()); Assert.Equal(path, f.GetProperty("Path").GetString());
        Assert.Equal(expected, f.GetProperty("Expected").GetString()); Assert.Equal(actual, f.GetProperty("Actual").GetString());
        if (phase is "PARSE_SCHEMA" or "PARSE_VALUES" or "PARSE_ENVELOPE") Assert.Equal("0", d.GetProperty("OkxCode").GetString());
        else Assert.Equal(JsonValueKind.Null, d.GetProperty("OkxCode").ValueKind);
    }
    private static Task<OkxEnrollmentDiscovery> Discover(Peer peer) => OkxEnrollmentDiscoveryClient.RunAsync("Demo", "DemoTradingApi", Encoding.ASCII.GetBytes(Peer.Key), Encoding.ASCII.GetBytes(Peer.Secret), Encoding.ASCII.GetBytes(Peer.Pass), null, null, default, peer);
    private static void AssertPrivateAbsent(string text)
    {
        foreach (string s in new[] { Peer.Key, Peer.Secret, Peer.Pass, Peer.Private, Peer.Uid, Peer.Ip, Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Peer.Key))).ToLowerInvariant() }) Assert.True(!text.Contains(s, StringComparison.Ordinal), "PRIVATE_SENTINEL_LEAK_WITHHELD");
    }
    private sealed class Peer(string scenario) : HttpMessageHandler
    {
        internal const string Key = "synthetic-payload-key", Secret = "synthetic-payload-secret", Pass = "synthetic-payload-pass", Private = "UNTRUSTED_PRIVATE_EXCEPTION_RAW_SENTINEL", Uid = "987654321098765432", Ip = "192.0.2.198";
        internal static readonly string[] Targets = { "/api/v5/public/time", "/api/v5/account/config", "/api/v5/account/balance", "/api/v5/account/instruments?instType=SPOT" };
        internal readonly List<string> Paths = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content); Assert.Equal("eea.okx.com", request.RequestUri!.Host); Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Equal("1", request.Headers.GetValues("x-simulated-trading").Single());
            string path = request.RequestUri.PathAndQuery; Paths.Add(path); Assert.Equal(Targets[Paths.Count - 1], path);
            if (Paths.Count > 1)
            {
                string ts = request.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
                Assert.Equal(OkxRequestSigner.Sign(Encoding.ASCII.GetBytes(Secret), ts, "GET", path, ReadOnlySpan<byte>.Empty), request.Headers.GetValues("OK-ACCESS-SIGN").Single());
            }
            if (scenario == "transport" && Paths.Count == 3) throw new HttpRequestException(Private);
            var dir = new DirectoryInfo(AppContext.BaseDirectory); while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/baseline-schema.json"))) dir = dir.Parent;
            var oracle = JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/baseline-schema.json")))!;
            string endpoint = new[] { "time", "account", "balance", "instruments" }[Paths.Count - 1];
            var body = oracle["endpoints"]![endpoint]!["control"]!.DeepClone(); var row = body["data"]![0]!;
            body["msg"] = Private;
            if (Paths.Count == 1) row["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Paths.Count == 2) { row["uid"] = Uid; row["mainUid"] = Uid; row["ip"] = Ip; row["label"] = Private; }
            if (scenario == "optional-absent")
                foreach (var field in oracle["endpoints"]![endpoint]!["fields"]!.AsArray())
                    if (field!["presence"]!.GetValue<string>().StartsWith("optional", StringComparison.Ordinal) && !field["field"]!.GetValue<string>().Contains('.')) row.AsObject().Remove(field["field"]!.GetValue<string>());
            switch (scenario)
            {
                case "time-type" when Paths.Count == 1: row["ts"] = 1; break;
                case "time-stamp" when Paths.Count == 1: row["ts"] = "99999999999999999"; break;
                case "time-semantic" when Paths.Count == 1: row["ts"] = ""; break;
                case "config-type" when Paths.Count == 2: row["autoLoan"] = "true"; break;
                case "config-missing" when Paths.Count == 2: row.AsObject().Remove("uid"); break;
                case "config-unknown" when Paths.Count == 2: row[Private] = Private; break;
                case "config-case" when Paths.Count == 2: row.AsObject().Remove("uid"); row["UID"] = Uid; break;
                case "config-semantic" when Paths.Count == 2: row["mainUid"] = "123"; break;
                case "balance-type" when Paths.Count == 3: row["details"]![0]!["cashBal"] = 123; break;
                case "balance-number" when Paths.Count == 3: row["details"]![0]!["cashBal"] = Private; break;
                case "balance-precision" when Paths.Count == 3: row["details"]![0]!["cashBal"] = "0.00000000000000000000000000001"; break;
                case "balance-stamp" when Paths.Count == 3: row["uTime"] = Private; break;
                case "balance-cardinality" when Paths.Count == 3: body["data"] = new JsonArray(); break;
                case "balance-details" when Paths.Count == 3: row.AsObject().Remove("details"); break;
                case "balance-semantic" when Paths.Count == 3: row["details"]![0]!["ccy"] = Private; break;
                case "balance-currencies" when Paths.Count == 3: row["details"]![1]!["ccy"] = row["details"]![0]!["ccy"]!.DeepClone(); break;
                case "instrument-type" when Paths.Count == 4: row["instIdCode"] = Private; break;
                case "instrument-missing" when Paths.Count == 4: row.AsObject().Remove("tradeQuoteCcyList"); break;
                case "instrument-array" when Paths.Count == 4: row["tradeQuoteCcyList"] = new JsonArray(new JsonObject()); break;
                case "instrument-nested" when Paths.Count == 4: row["upcChg"]![0]!["effTime"] = 1; break;
                case "instrument-number" when Paths.Count == 4: row["lotSz"] = Private; break;
                case "instrument-semantic" when Paths.Count == 4: row["instId"] = Private; break;
                case "instrument-identities" when Paths.Count == 4: body["data"]!.AsArray().Add(row.DeepClone()); break;
            }
            string json = body.ToJsonString();
            if (scenario == "config-duplicate" && Paths.Count == 2) json = json.Replace("\"uid\":", "\"uid\":\"1\",\"UID\":");
            if (scenario == "balance-duplicate" && Paths.Count == 3) json = json.Replace("\"cashBal\":", "\"cashBal\":\"1\",\"cashBal\":");
            if (scenario == "config-string" && Paths.Count == 2) json = json.Replace("\"label\":\"" + Private + "\"", "\"label\":\"\\uDEAD\"");
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            if (scenario == "instrument-utf8" && Paths.Count == 4) bytes = new byte[] { 0xff };
            if (scenario == "instrument-json" && Paths.Count == 4) bytes = Encoding.UTF8.GetBytes("{\"" + Private);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent(bytes) });
        }
    }
}
