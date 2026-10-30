using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Xunit;

namespace Apex.Tests;
public sealed class NavalReadEndpointTests
{
    // These exercise the production converter: TryParse success alone must not admit rounded values.
    [Theory]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("-0.0000000000000000000000000001", "-0.0000000000000000000000000001")]
    [InlineData("0.1234567890123456789012345678", "0.1234567890123456789012345678")]
    [InlineData("-0.1234567890123456789012345678", "-0.1234567890123456789012345678")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335", "-79228162514264337593543950335")]
    [InlineData("79228162514264337593543950335.0000", "79228162514264337593543950335")]
    [InlineData("7.9228162514264337593543950335", "7.9228162514264337593543950335")]
    [InlineData("10039.160000000000000000000000000000000000000000", "10039.16")]
    [InlineData("+0001.2300", "1.23")]
    [InlineData("+.5", "0.5")]
    [InlineData("1.", "1")]
    [InlineData("-000.000000000000000000000000000000000000", "0")]
    public void NavalReadEndpoint_DecimalAdmissionPreservesExactValues(string raw, string expected)
    {
        decimal actual = JsonSerializer.Deserialize<decimal>(JsonSerializer.Serialize(raw), Apex.Desktop.Naval.NavalPublicationReader.Json);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), actual);
    }
    [Theory]
    [InlineData("10039.16000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001")]
    [InlineData("-10039.16000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001")]
    [InlineData("0.12345678901234567890123456781")]
    [InlineData("-0.12345678901234567890123456781")]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("-0.00000000000000000000000000001")]
    [InlineData("0.00000000000000000000000000005")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("-79228162514264337593543950336")]
    [InlineData("79228162514264337593543950335.1")]
    [InlineData("-79228162514264337593543950335.1")]
    [InlineData("7.9228162514264337593543950336")]
    [InlineData("1e-28")]
    [InlineData("1,000")]
    [InlineData(" 1")]
    [InlineData("1\u0000")]
    public void NavalReadEndpoint_DecimalAdmissionRejectsInexactOrInvalidValues(string raw)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<decimal>(JsonSerializer.Serialize(raw), Apex.Desktop.Naval.NavalPublicationReader.Json));
    }
    [Fact]
    public void NavalReadEndpoint_DecimalAdmissionHandlesLongValuePreservingZeroPadding()
    {
        string raw = "+" + new string('0', 4096) + "1.25" + new string('0', 4096);
        Assert.Equal(1.25m, JsonSerializer.Deserialize<decimal>(JsonSerializer.Serialize(raw), Apex.Desktop.Naval.NavalPublicationReader.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<decimal>(JsonSerializer.Serialize(raw + "1"), Apex.Desktop.Naval.NavalPublicationReader.Json));
    }
    [Theory]
    [InlineData("cash")]
    [InlineData("equity")]
    [InlineData("reservationQuantity")]
    [InlineData("fillPrice")]
    [InlineData("fillFee")]
    [InlineData("journalFee")]
    public async Task NavalReadEndpoint_ChecksumValidInexactNestedDecimalsNeverAdmitted(string field)
    {
        await using var h = new NavalProductionHost(); await NavalHostPublication.Cli(h);
        string path = NavalHostPublication.PathFor(h); var n = JsonNode.Parse(File.ReadAllText(path))!;
        JsonNode Target(JsonNode envelope)
        {
            var j = envelope["publication"]!["journal"]!; var cp = j["checkpoint"]!;
            return field switch
            {
                "cash" => cp["accounts"]![0]!["cash"]!,
                "equity" => cp["historicalLiquidationEquity"]!,
                "reservationQuantity" => cp["fleets"]![0]!["reservations"]![0]!["reservation"]!,
                "fillPrice" or "fillFee" => cp["fleets"]![0]!["fills"]![0]!["fill"]!,
                _ => j["batches"]!.AsArray().SelectMany(b => b!["facts"]!.AsArray()).First(f => f!["fill"] != null)!["fill"]!["fill"]!
            };
        }
        string key = field switch { "cash" or "equity" => "amount", "reservationQuantity" => "quantity", "fillPrice" => "price", _ => "fee" };
        Target(n)[key] = decimal.Parse(Target(n)[key]!.GetValue<string>(), CultureInfo.InvariantCulture).ToString("F28", CultureInfo.InvariantCulture);
        NavalHostPublication.RehashJournal(n);
        // Freeze a valid typed control at the greatest scale its coefficient supports. Extra
        // nonzero precision then rounds to identical typed checksum bytes on the defective reader.
        n = JsonSerializer.SerializeToNode(JsonSerializer.Deserialize<Apex.Desktop.Naval.NavalPublicationEnvelope>(n.ToJsonString(), NavalHostPublication.Json), NavalHostPublication.Json)!;
        string control = n.ToJsonString(); string controlAmount = Target(n)[key]!.GetValue<string>();
        Target(n)[key] = controlAmount + new string('0', 70) + "1";
        string malformed = n.ToJsonString();
        Assert.Equal(JsonNode.Parse(control)!["contentSha256"]!.GetValue<string>(), n["contentSha256"]!.GetValue<string>());
        Assert.Equal(JsonNode.Parse(control)!["publication"]!["journal"]!["integrityDigest"]!.GetValue<string>(), n["publication"]!["journal"]!["integrityDigest"]!.GetValue<string>());
        File.WriteAllText(path, malformed); await h.Start();
        // Readiness touches only root; malformed is the first Naval capture, not cached fallback.
        using var response = await h.Send("/api/naval/snapshot", "read");
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        File.WriteAllText(path, control); var good = await Get(h, "/api/naval/snapshot");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(control)!["publication"]!["journal"]!["checkpoint"], good["checkpoint"]));
        NavalProductionHost.Evidence("decimal-pair-" + field + "-" + Guid.NewGuid().ToString("N"), new { field, control = JsonNode.Parse(control), malformed = JsonNode.Parse(malformed), malformedStatus = (int)response.StatusCode, validStatus = 200, malformedResponse = error, validResponse = good });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("PUBLICATION_INVALID", error["reasonCode"]!.GetValue<string>());
        Assert.Null(error["checkpoint"]); Assert.Null(error["financialRevision"]); Assert.Null(error["broker"]!["equity"]);
    }
    [Fact]
    public async Task NavalReadEndpoint_TestExportMatchesProducerChecksum()
    {
        await using var h = new NavalProductionHost(); using var f = new NavalCashFixture(); f.Run(); NavalHostPublication.Export(h, f.C.ReadPublication()!, f.Clock.Now);
        var options = JsonSerializer.Deserialize<Apex.Desktop.Naval.NavalReadOptions>(h.Configuration["NavalRead"]!.ToJsonString(), Apex.Desktop.Services.ConfigStore.Json)!;
        var runtime = Apex.Desktop.Naval.NavalAccessPolicy.Load(options)!;
        Assert.NotNull(Apex.Desktop.Naval.NavalPublicationReader.Parse(File.ReadAllBytes(NavalHostPublication.PathFor(h)), runtime.Scope));
    }
    [Fact]
    public async Task NavalReadEndpoint_SnapshotUsesCommittedPublication()
    {
        await using var h = new NavalProductionHost(); await NavalHostPublication.Cli(h); await h.Start();
        var snapshot = await Get(h, "/api/naval/snapshot?requestGeneration=test_1");
        Assert.Equal("naval-host-read-v1", snapshot["schemaVersion"]!.GetValue<string>()); Assert.Equal("test_1", snapshot["requestGeneration"]!.GetValue<string>());
        var p = JsonNode.Parse(File.ReadAllText(NavalHostPublication.PathFor(h)))!["publication"]!;
        Assert.True(JsonNode.DeepEquals(p["journal"]!["checkpoint"], snapshot["checkpoint"]));
        Assert.Equal(p["financialRevision"]!.GetValue<string>(), snapshot["snapshotVersion"]!.GetValue<string>());
        Assert.Equal("NOT_CONFIGURED", snapshot["broker"]!["state"]!.GetValue<string>()); Assert.Null(snapshot["broker"]!["equity"]);
        Assert.True(snapshot["sources"]![0]!["stale"]!.GetValue<bool>());
        Assert.Equal("UNKNOWN", snapshot["broker"]!["runtimeReadiness"]!.GetValue<string>());
        string bytes = snapshot.ToJsonString(); Assert.True(!bytes.Contains(h.DirectoryPath) && !bytes.Contains(h.ReadSecret) && !bytes.Contains(h.LegacyToken) && !bytes.Contains("pairingKey"));
    }
    [Fact]
    public async Task NavalReadEndpoint_NoAccountIsNotZeroOrCorruptSuccess()
    {
        await using var h = new NavalProductionHost(); await h.Start(); var s = await Get(h, "/api/naval/snapshot");
        Assert.Equal("NOT_CONFIGURED", s["availability"]!.GetValue<string>()); Assert.Null(s["checkpoint"]); Assert.Null(s["cursor"]); Assert.Null(s["financialRevision"]);
        string cursor = new NavalCursor(Guid.NewGuid().ToString("N"), new string('a', 64), 0).Encode();
        var e = await Get(h, "/api/naval/events?hostEpoch=" + s["hostEpoch"]!.GetValue<string>() + "&cursor=" + cursor);
        Assert.Null(e["page"]); Assert.True(e["resetRequired"]!.GetValue<bool>());
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("hash")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missingGetter")]
    [InlineData("getter")]
    [InlineData("null")]
    [InlineData("numeric")]
    [InlineData("permissions")]
    [InlineData("oversize")]
    [InlineData("identity")]
    public async Task NavalReadEndpoint_ConfiguredInvalidSourceNeverServesMoney(string fault)
    {
        await using var h = new NavalProductionHost(); await NavalHostPublication.Cli(h); string path = NavalHostPublication.PathFor(h); var n = JsonNode.Parse(File.ReadAllText(path))!;
        switch (fault)
        {
            case "missing": File.Delete(path); break;
            case "truncated": File.WriteAllText(path, "{"); break;
            case "hash": n["contentSha256"] = new string('0', 64); break;
            case "unknown": n["unexpected"] = true; break;
            case "duplicate": File.WriteAllText(path, File.ReadAllText(path).Replace("\"schema\":", "\"schema\":\"invalid\",\"schema\":")); break;
            case "missingGetter": n["publication"]!["journal"]!["checkpoint"]!.AsObject().Remove("currentQuality"); break;
            case "getter": n["publication"]!["journal"]!["checkpoint"]!["brokerState"] = "READY"; break;
            case "null": n["publication"]!["journal"]!["checkpoint"]!["accounts"] = null; break;
            case "numeric": n["publication"]!["financialRevision"] = 18; break;
            case "permissions": if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead); break;
            case "oversize": using (var f = new FileStream(path, FileMode.Open)) f.SetLength(32 * 1024 * 1024 + 1); break;
            case "identity": h.Configuration["NavalRead"]!["Scope"]!["PublicationScopeId"] = new string('f', 64); break;
        }
        if (fault is not ("missing" or "truncated" or "duplicate" or "permissions" or "oversize" or "identity")) File.WriteAllText(path, n.ToJsonString());
        // Publication permissions are a source fault, not credential/startup failure.
        await h.Start();
        if (fault == "permissions" && OperatingSystem.IsWindows())
        {
            // Mutate after native startup admission: this tests the passive reader's
            // source-fault refusal, not the separate whole-tree startup guard.
            var file = new FileInfo(path); var acl = file.GetAccessControl();
            var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            acl.AddAccessRule(new FileSystemAccessRule(world, FileSystemRights.Read, AccessControlType.Allow));
            file.SetAccessControl(acl);
            bool broad = false;
            foreach (FileSystemAccessRule rule in file.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
                broad |= rule.IdentityReference.Equals(world) && rule.AccessControlType == AccessControlType.Allow;
            Assert.True(broad, "The fixture must actually add a broad Windows read ACL");
        }
        var response = await h.Send("/api/naval/snapshot", "read"); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!; Assert.Null(body["checkpoint"]); Assert.True(!body.ToJsonString().Contains(h.DirectoryPath));
    }
    [Fact]
    public async Task NavalReadEndpoint_ResumesUsingAcceptedReadPage()
    {
        await using var h = new NavalProductionHost(); await NavalHostPublication.Cli(h); await h.Start(); var s = await Get(h, "/api/naval/snapshot");
        string host = s["hostEpoch"]!.GetValue<string>(); var current = NavalCursor.Decode(s["cursor"]!.GetValue<string>());
        string Url(NavalCursor c, string epoch) => "/api/naval/events?hostEpoch=" + epoch + "&cursor=" + c.Encode() + "&limit=1";
        var e = await Get(h, Url(current with { Sequence = 0 }, host)); Assert.Equal("1", e["page"]!["batches"]![0]!["sequence"]!.GetValue<string>()); Assert.True(e["page"]!["hasMore"]!.GetValue<bool>());
        Assert.Equal(1, NavalCursor.Decode(e["nextCursorEncoded"]!.GetValue<string>()).Sequence);
        var empty = await Get(h, Url(current, host)); Assert.Empty(empty["page"]!["batches"]!.AsArray()); Assert.Equal(current.Encode(), empty["nextCursorEncoded"]!.GetValue<string>());
        var gap = await Get(h, Url(current with { Sequence = current.Sequence + 1 }, host)); Assert.True(gap["page"]!["gap"]!.GetValue<bool>()); Assert.Empty(gap["page"]!["batches"]!.AsArray());
        Assert.Equal(HttpStatusCode.Conflict, (await h.Send(Url(current, Guid.NewGuid().ToString("N")), "read")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Send(Url(current with { ScopeId = new string('f', 64) }, host), "read")).StatusCode);
        foreach (string query in new[] { "", "?cursor=x", "?hostEpoch=" + host + "&cursor=x", "?hostEpoch=" + host + "&cursor=" + current.Encode() + "&limit=01", "?hostEpoch=" + host + "&cursor=" + current.Encode() + "&limit=101", "?hostEpoch=" + host + "&cursor=" + current.Encode() + "&limit=1&limit=1" }) Assert.Equal(HttpStatusCode.BadRequest, (await h.Send("/api/naval/events" + query, "read")).StatusCode);
        foreach (string query in new[] { "?scope=offline&scope=offline", "?path=anything", "?requestGeneration=", "?requestGeneration=bad%20value" }) Assert.Equal(HttpStatusCode.BadRequest, (await h.Send("/api/naval/snapshot" + query, "read")).StatusCode);
    }
    [Fact]
    public async Task NavalReadEndpoint_RestartRequiresSnapshotWithoutReplay()
    {
        await using var h = new NavalProductionHost(); await NavalHostPublication.Cli(h); await h.Start(); var s = await Get(h, "/api/naval/snapshot"); await h.Stop(); await h.Start();
        var after = await Get(h, "/api/naval/snapshot"); Assert.NotEqual(s["hostEpoch"]!.GetValue<string>(), after["hostEpoch"]!.GetValue<string>()); Assert.True(JsonNode.DeepEquals(s["checkpoint"], after["checkpoint"])); Assert.Equal(s["cursor"]!.GetValue<string>(), after["cursor"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await h.Send("/api/naval/events?hostEpoch=" + s["hostEpoch"]!.GetValue<string>() + "&cursor=" + s["cursor"]!.GetValue<string>(), "read")).StatusCode);
    }
    [Fact]
    public async Task NavalReadEndpoint_HistoricalNeverBecomesLiveFresh()
    {
        await using var h = new NavalProductionHost(); await NavalHostPublication.Cli(h); await h.Start(); var a = await Get(h, "/api/naval/snapshot");
        var n = JsonNode.Parse(File.ReadAllText(NavalHostPublication.PathFor(h)))!; n["sourceClockUtc"] = DateTimeOffset.UtcNow.AddDays(1).ToString("O"); NavalHostPublication.Rehash(n); File.WriteAllText(NavalHostPublication.PathFor(h), n.ToJsonString());
        var b = await Get(h, "/api/naval/snapshot"); Assert.Equal(a["observedUtc"]!.GetValue<string>(), b["observedUtc"]!.GetValue<string>()); Assert.Equal(a["sources"]![0]!["sourceClockUtc"]!.GetValue<string>(), b["sources"]![0]!["sourceClockUtc"]!.GetValue<string>()); Assert.Equal("HISTORICAL", b["sources"]![0]!["freshness"]!.GetValue<string>()); Assert.True(b["sources"]![0]!["stale"]!.GetValue<bool>()); Assert.Equal(a["snapshotVersion"]!.GetValue<string>(), b["snapshotVersion"]!.GetValue<string>());
    }
    [Fact]
    public async Task NavalReadEndpoint_RealWriterLeaseAndPersistenceRemainUntouched()
    {
        await using var h = new NavalProductionHost(); using var f = new NavalCashFixture(); f.Owner.Dispose(); var authority = Path.Combine(f.DirectoryPath, "disk"); Directory.CreateDirectory(authority); var disk = new NavalHostPublication.DiskStore(authority); f.Owner = new(authority, disk, f.D); f.C = new(f.Owner, f.D, f.Clock); f.Run(); var r = f.Reserve(); f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm, fills: new[] { f.Fill(r, 2, 100, .2m) }));
        var p = f.C.ReadPublication()!; NavalHostPublication.Export(h, p, f.Clock.Now);
        var fileSet = Directory.GetFiles(authority).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        var before = Directory.GetFiles(authority).Where(p => Path.GetFileName(p) != ".naval-owner.lock").ToDictionary(p => Path.GetFileName(p)!, p => File.ReadAllBytes(p));
        Assert.ThrowsAny<Exception>(() => new NavalCoreStateOwner(authority, new NavalHostPublication.DiskStore(authority), f.D));
        await h.Start(); for (int i = 0; i < 3; i++) await Get(h, "/api/naval/snapshot");
        var snap = await Get(h, "/api/naval/snapshot"); await Get(h, "/api/naval/events?hostEpoch=" + snap["hostEpoch"]!.GetValue<string>() + "&cursor=" + snap["cursor"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Send("/api/naval/receipt", "read", "POST")).StatusCode); Assert.Equal(HttpStatusCode.MethodNotAllowed, (await h.Send("/api/naval/snapshot", "operator", "POST")).StatusCode);
        File.WriteAllText(NavalHostPublication.PathFor(h), "{"); Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Send("/api/naval/snapshot", "read")).StatusCode);
        await h.Stop(); NavalHostPublication.Export(h, p, f.Clock.Now); await h.Start(); await Get(h, "/api/naval/snapshot");
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(authority, pair.Key!))); Assert.Equal(fileSet, Directory.GetFiles(authority).Select(Path.GetFileName).OrderBy(x => x)); Assert.Equal(p.FinancialRevision, f.C.ReadPublication()!.FinancialRevision);
        var after = Directory.GetFiles(authority).Where(path => Path.GetFileName(path) != ".naval-owner.lock").ToDictionary(path => Path.GetFileName(path)!, path => File.ReadAllBytes(path));
        NavalProductionHost.Evidence("held-writer-" + Guid.NewGuid().ToString("N"), new { secondRealOwnerDenied = true, authorityFileSet = fileSet, beforeHashes = before.ToDictionary(k => k.Key, k => Convert.ToHexString(SHA256.HashData(k.Value))), afterHashes = after.ToDictionary(k => k.Key, k => Convert.ToHexString(SHA256.HashData(k.Value))), publicationBefore = p, publicationAfter = f.C.ReadPublication() });
    }
    [Fact]
    public async Task NavalReadEndpoint_AtomicCaptureAndIngestOrdering()
    {
        await using var h = new NavalProductionHost(); using var f = new NavalCashFixture(); f.Run(); var old = f.C.ReadPublication()!; NavalHostPublication.Export(h, old, f.Clock.Now); await h.Start(); var first = await Get(h, "/api/naval/snapshot");
        File.WriteAllText(NavalHostPublication.PathFor(h), "{"); Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Send("/api/naval/snapshot", "read")).StatusCode); NavalHostPublication.Export(h, old, f.Clock.Now); await Get(h, "/api/naval/snapshot");
        f.C.Stop("OFFLINE_STOP"); var newer = f.C.ReadPublication()!; NavalHostPublication.Export(h, newer, f.Clock.Now); var next = await Get(h, "/api/naval/snapshot"); Assert.Equal(newer.FinancialRevision.ToString(CultureInfo.InvariantCulture), next["snapshotVersion"]!.GetValue<string>());
        NavalHostPublication.Export(h, old, f.Clock.Now); Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Send("/api/naval/snapshot", "read")).StatusCode); NavalHostPublication.Export(h, newer, f.Clock.Now); Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Send("/api/naval/snapshot", "read")).StatusCode);
    }
    [Theory]
    [InlineData("duplicateFill")]
    [InlineData("coverage")]
    [InlineData("operation")]
    [InlineData("checkpointMode")]
    [InlineData("checkpointCode")]
    [InlineData("enumCase")]
    public async Task NavalReadEndpoint_ChecksumValidUnsafeMetadataIsRejected(string fault)
    {
        await using var h = new NavalProductionHost(); await NavalHostPublication.Cli(h); await h.Start();
        var n = JsonNode.Parse(File.ReadAllText(NavalHostPublication.PathFor(h)))!; var batches = n["publication"]!["journal"]!["batches"]!.AsArray(); var facts = batches.SelectMany(b => b!["facts"]!.AsArray()).ToArray();
        if (fault == "duplicateFill")
        {
            var target = batches.Last()!; var list = target["facts"]!.AsArray(); var clone = facts.First(f => f!["fill"] != null)!.DeepClone(); clone["factId"] = target["eventId"]!.GetValue<string>() + ":" + list.Count; list.Add(clone);
        }
        if (fault == "coverage") { var fact = facts.First(f => f!["coverage"] != null)!; fact["reservation"]!["reservation"]!["terminalCoverage"] = false; }
        if (fault == "operation") facts.First(f => f!["operation"] != null)!["operation"]!["resultCode"] = "PRIVATE_SENTINEL";
        if (fault == "enumCase")
        { var reservation = n["publication"]!["journal"]!["checkpoint"]!["fleets"]!.AsArray().SelectMany(f => f!["reservations"]!.AsArray()).First()!["reservation"]!; reservation["stage"] = reservation["stage"]!.GetValue<string>().ToLowerInvariant(); }
        if (fault == "checkpointMode") n["publication"]!["journal"]!["checkpoint"]!["durableMode"] = "READY";
        if (fault == "checkpointCode") n["publication"]!["journal"]!["checkpoint"]!["reasonCode"] = "PRIVATE_SENTINEL";
        NavalHostPublication.RehashJournal(n); File.WriteAllText(NavalHostPublication.PathFor(h), n.ToJsonString());
        var r = await h.Send("/api/naval/snapshot", "read"); Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode); Assert.True(!(await r.Content.ReadAsStringAsync()).Contains("PRIVATE_SENTINEL"));
    }
    [Theory]
    [InlineData("checkpoint")]
    [InlineData("overlap")]
    [InlineData("epoch")]
    [InlineData("scope")]
    public async Task NavalReadEndpoint_ValidConflictOrLineageReplacementLatches(string fault)
    {
        await using var h = new NavalProductionHost(); await NavalHostPublication.Cli(h); await h.Start(); await Get(h, "/api/naval/snapshot"); string original = File.ReadAllText(NavalHostPublication.PathFor(h)); var n = JsonNode.Parse(original)!; var j = n["publication"]!["journal"]!;
        if (fault == "checkpoint") j["checkpoint"]!["durableMode"] = "STOP";
        if (fault == "overlap") j["batches"]![0]!["committedUtc"] = DateTimeOffset.Parse(j["batches"]![0]!["committedUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture).AddTicks(1).ToString("O");
        if (fault == "epoch")
        {
            string epoch = Guid.NewGuid().ToString("N"); j["epoch"] = epoch;
            foreach (var b in j["batches"]!.AsArray()) { string id = epoch + ":" + b!["sequence"]!.GetValue<string>(); b["eventId"] = id; int i = 0; foreach (var f in b["facts"]!.AsArray()) f!["factId"] = id + ":" + i++; }
        }
        if (fault == "scope")
        {
            j["checkpoint"]!["configDigest"] = new string('d', 64);
            var cp = JsonSerializer.Deserialize<NavalReadCheckpoint>(j["checkpoint"]!.ToJsonString(), NavalHostPublication.Json)!;
            j["scopeId"] = NavalJson.Hash(new { cp.PortfolioId, cp.ConfigDigest, Accounts = cp.Accounts.Select(a => a.Account).ToArray() });
        }
        NavalHostPublication.RehashJournal(n); File.WriteAllText(NavalHostPublication.PathFor(h), n.ToJsonString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Send("/api/naval/snapshot", "read")).StatusCode); File.WriteAllText(NavalHostPublication.PathFor(h), original);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Send("/api/naval/snapshot", "read")).StatusCode);
    }
    [Fact]
    public async Task NavalReadEndpoint_GenuineRetentionAndConcurrentAtomicReplacement()
    {
        await using var h = new NavalProductionHost(); using var f = new NavalCashFixture(); f.Run(); var old = f.C.ReadPublication()!; NavalHostPublication.Export(h, old, f.Clock.Now); await h.Start(); await Get(h, "/api/naval/snapshot");
        for (int i = 0; i < 515; i++) f.C.Stop("OFFLINE_STOP"); var next = f.C.ReadPublication()!;
        var reads = Enumerable.Range(0, 12).Select(async _ => { var s = await Get(h, "/api/naval/snapshot"); string v = s["snapshotVersion"]!.GetValue<string>(); Assert.True(v == old.FinancialRevision.ToString(CultureInfo.InvariantCulture) || v == next.FinancialRevision.ToString(CultureInfo.InvariantCulture)); Assert.Equal(v, s["checkpoint"]!["financialRevision"]!.GetValue<string>()); });
        var pending = Task.WhenAll(reads); NavalHostPublication.Export(h, next, f.Clock.Now); await pending; var latest = await Get(h, "/api/naval/snapshot");
        var cursor = new NavalCursor(next.Journal.Epoch, next.Journal.ScopeId, 0); var e = await Get(h, "/api/naval/events?hostEpoch=" + latest["hostEpoch"]!.GetValue<string>() + "&cursor=" + cursor.Encode());
        Assert.True(e["page"]!["gap"]!.GetValue<bool>()); Assert.True(e["page"]!["resetRequired"]!.GetValue<bool>()); Assert.Empty(e["page"]!["batches"]!.AsArray());
    }
    [Fact]
    public async Task NavalReadEndpoint_LargeCountersAndUnknownFutureCaptureRemainStrings()
    {
        await using var h = new NavalProductionHost(); await NavalHostPublication.Cli(h); var n = JsonNode.Parse(File.ReadAllText(NavalHostPublication.PathFor(h)))!; var pub = n["publication"]!; var j = pub["journal"]!;
        const long baseline = 9007199254740993L; long last = long.Parse(j["lastSequence"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        j["baselineRevision"] = baseline.ToString(CultureInfo.InvariantCulture); pub["financialRevision"] = (baseline + last).ToString(CultureInfo.InvariantCulture); j["checkpoint"]!["financialRevision"] = (baseline + last).ToString(CultureInfo.InvariantCulture);
        foreach (var b in j["batches"]!.AsArray()) b!["financialRevision"] = (baseline + long.Parse(b["sequence"]!.GetValue<string>(), CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
        n["sourceClockUtc"] = DateTimeOffset.UtcNow.AddYears(1).ToString("O"); NavalHostPublication.RehashJournal(n); File.WriteAllText(NavalHostPublication.PathFor(h), n.ToJsonString()); await h.Start(); var s = await Get(h, "/api/naval/snapshot");
        Assert.Equal((baseline + last).ToString(CultureInfo.InvariantCulture), s["snapshotVersion"]!.GetValue<string>()); Assert.Equal("UNKNOWN", s["sources"]![0]!["freshness"]!.GetValue<string>()); Assert.Null(s["sources"]![0]!["ageSeconds"]); Assert.True(s["sources"]![0]!["stale"]!.GetValue<bool>());
    }
    [Fact]
    public async Task NavalReadEndpoint_MissingCaptureCannotClaimHistoricalTimestampKnowledge()
    {
        await using var h = new NavalProductionHost(); await NavalHostPublication.Cli(h); var n = JsonNode.Parse(File.ReadAllText(NavalHostPublication.PathFor(h)))!; var j = n["publication"]!["journal"]!;
        foreach (var b in j["batches"]!.AsArray()) { b!["captureUtc"] = null; b["receiveUtc"] = null; }
        j["checkpoint"]!["reconciledUtc"] = null;
        NavalHostPublication.RehashJournal(n); File.WriteAllText(NavalHostPublication.PathFor(h), n.ToJsonString()); await h.Start(); var s = await Get(h, "/api/naval/snapshot");
        Assert.Equal("UNKNOWN", s["sources"]![0]!["freshness"]!.GetValue<string>()); Assert.Null(s["sources"]![0]!["ageSeconds"]); Assert.Equal("UNKNOWN", s["broker"]!["runtimeReadiness"]!.GetValue<string>());
    }
    internal static async Task<JsonNode> Get(NavalProductionHost h, string url)
    { var r = await h.Send(url, "read"); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return JsonNode.Parse(await r.Content.ReadAsStringAsync())!; }
}

internal static class NavalHostPublication
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new Counter(), new Money(), new JsonStringEnumConverter(null, false) } };
    private sealed class Counter : JsonConverter<long> { public override long Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => long.Parse(r.GetString()!, CultureInfo.InvariantCulture); public override void Write(Utf8JsonWriter w, long v, JsonSerializerOptions o) => w.WriteStringValue(v.ToString(CultureInfo.InvariantCulture)); }
    private sealed class Money : JsonConverter<decimal> { public override decimal Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => decimal.Parse(r.GetString()!, CultureInfo.InvariantCulture); public override void Write(Utf8JsonWriter w, decimal v, JsonSerializerOptions o) => w.WriteStringValue(v.ToString(CultureInfo.InvariantCulture)); }
    internal static string PathFor(NavalProductionHost h) => Path.Combine(h.DirectoryPath, "publication", "publication.json");
    internal static async Task Cli(NavalProductionHost h)
    {
        string directory = Path.GetDirectoryName(PathFor(h))!; NavalProductionHost.PrivateDirectory(directory);
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/home/ubuntu/.dotnet/dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { Path.Combine(NavalProductionHost.Repo, "tools", "Apex.Backtester", "bin", "Release", "net8.0", "Apex.Backtester.dll"), "naval-shared-replay", "--input", Path.Combine(NavalProductionHost.Repo, "tests", "Apex.Backtester.Tests", "fixtures", "synthetic", "naval-shared-replay-v1.json"), "--state-dir", Path.Combine(h.DirectoryPath, "authority"), "--output", Path.Combine(h.DirectoryPath, "result.json"), "--publication", PathFor(h) }) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!; var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Assert.Equal(0, p.ExitCode); await Task.WhenAll(stdout, stderr); } finally { if (!p.HasExited) { p.Kill(true); await p.WaitForExitAsync(); } }
        NavalProductionHost.Evidence("cli-publication-" + Guid.NewGuid().ToString("N"), JsonNode.Parse(File.ReadAllText(PathFor(h))));
        Configure(h, JsonSerializer.Deserialize<NavalReadPublication>(JsonNode.Parse(File.ReadAllText(PathFor(h)))!["publication"]!.ToJsonString(), Json)!);
    }
    internal static void Configure(NavalProductionHost h, NavalReadPublication p)
    {
        var c = p.Journal.Checkpoint; var pascal = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        h.Configuration["NavalRead"]!["PublicationPath"] = PathFor(h);
        h.Configuration["NavalRead"]!["Scope"] = JsonSerializer.SerializeToNode(new { Id = "offline", PublicationScopeId = p.Journal.ScopeId, c.PortfolioId, c.ConfigDigest, Accounts = c.Accounts.Select(a => a.Account).ToArray() }, pascal);
    }
    internal static void Export(NavalProductionHost h, NavalReadPublication p, DateTimeOffset clock)
    {
        NavalProductionHost.PrivateDirectory(Path.GetDirectoryName(PathFor(h))!); Configure(h, p);
        var n = JsonSerializer.SerializeToNode(new { schema = "naval-shared-publication-export-v1", temporalClassification = "HISTORICAL_OFFLINE_CHECKPOINT", sourceClockUtc = clock, sourceFreshness = "UNKNOWN", provenance = new { inputSha256 = new string('a', 64), coreSha256 = new string('b', 64), backtesterSha256 = new string('c', 64) }, publication = p }, Json)!;
        Rehash(n); string tmp = PathFor(h) + ".candidate"; File.WriteAllText(tmp, n.ToJsonString(Json)); NavalProductionHost.PrivateFile(tmp);
        // Native read-side ACL inspection briefly holds the leaf without delete
        // sharing. Keep the candidate intact and retry atomic rename, never write
        // in place or weaken the production path guard. Permanent refusal fails.
        long deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            try { File.Move(tmp, PathFor(h), true); break; }
            catch (Exception ex) when (OperatingSystem.IsWindows() && (ex is IOException or UnauthorizedAccessException) && Environment.TickCount64 < deadline)
            { System.Threading.Thread.Yield(); }
        }
    }
    internal static void RehashJournal(JsonNode envelope)
    {
        var journal = envelope["publication"]!["journal"]!; journal["integrityDigest"] = "";
        journal["integrityDigest"] = NavalJson.Hash(JsonSerializer.Deserialize<NavalReadJournalState>(journal.ToJsonString(), Json)!);
        Rehash(envelope);
    }
    private sealed record Provenance(string InputSha256, string CoreSha256, string BacktesterSha256);
    private sealed record Content(string Schema, string TemporalClassification, DateTimeOffset SourceClockUtc, string SourceFreshness, Provenance Provenance, NavalReadPublication Publication);
    internal static void Rehash(JsonNode n)
    {
        n.AsObject().Remove("contentSha256");
        var content = JsonSerializer.Deserialize<Content>(n.ToJsonString(), Json)!;
        n["contentSha256"] = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, Json))).ToLowerInvariant();
    }
    internal sealed class DiskStore : IKeyValueStore
    { private readonly string path; internal DiskStore(string directory) { path = Path.Combine(directory, "authority.json"); } public string? Get(string key) => File.Exists(path) ? File.ReadAllText(path) : null; public void Set(string key, string value) { File.WriteAllText(path, value); } public void Flush() { } }
}
