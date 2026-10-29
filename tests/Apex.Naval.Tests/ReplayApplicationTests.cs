using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Apex.Core.Naval;
using Apex.Naval.Replay;
using Xunit;

namespace Apex.Naval.Tests;

public sealed class ReplayApplicationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "apex-naval-offline", "test-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    public ReplayApplicationTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string Input(params NavalEvaluationRequest[] requests)
    {
        var path = Path.Combine(directory, "input.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = "naval-replay-v1", datasetLabel = "SYNTHETIC — NOT MARKET HISTORY",
            requests = requests.Length == 0 ? new[] { NavalD1EvaluatorTests.Request() } : requests
        }, Json));
        return path;
    }
    private (int Code, string Stdout, string Stderr) Run(params string[] args)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        return (new ReplayApplication().Run(args, output, error), output.ToString(), error.ToString());
    }
    private string Output(string name = "output.json") => Path.Combine(directory, name);

    [Fact]
    public void CliWritesOnlyOfflineDecisions()
    {
        var request = NavalD1EvaluatorTests.Request();
        var input = Input(request, request with { Fleet = NavalFleet.Bravo }, request,
            request with { HasLong = true, Instrument = NavalD1EvaluatorTests.Series(last: 80m), Btc = null });
        var result = Run("--input", input, "--output", Output());
        Assert.Equal(0, result.Code); Assert.Equal("", result.Stderr);
        Assert.True(File.Exists(Output()), "Successful replay must publish its output artifact");
        using var document = JsonDocument.Parse(File.ReadAllBytes(Output()));
        var root = document.RootElement;
        Assert.Equal("OFFLINE", root.GetProperty("mode").GetString());
        Assert.Equal("NO_EXECUTION", root.GetProperty("execution").GetString());
        Assert.Equal("naval-replay-result-v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))).ToLowerInvariant(), root.GetProperty("inputSha256").GetString());
        var decisions = root.GetProperty("decisions").EnumerateArray().ToArray();
        Assert.Equal(4, decisions.Length);
        Assert.Equal(new[] { "EntrySignal", "EntrySignal", "EntrySignal", "ExitSignal" }, decisions.Select(d => d.GetProperty("action").GetString()));
        Assert.Equal(new[] { "Alpha", "Bravo", "Alpha", "Alpha" }, decisions.Select(d => d.GetProperty("fleet").GetString()));
        Assert.NotEmpty(decisions[0].GetProperty("stableId").GetString()!);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicateArg")]
    [InlineData("missingValue")]
    [InlineData("wrongSchema")]
    [InlineData("missingHolding")]
    [InlineData("missingConfirmed")]
    [InlineData("missingSettings")]
    [InlineData("unknownJson")]
    [InlineData("duplicateJson")]
    [InlineData("numericFleet")]
    [InlineData("malformed")]
    [InlineData("noRequests")]
    public void CliRejectsUnknownArgumentsAndWrongSchema(string fault)
    {
        var input = Input();
        var node = JsonNode.Parse(File.ReadAllText(input))!;
        switch (fault)
        {
            case "wrongSchema": node["schemaVersion"] = "other"; break;
            case "missingHolding": node["requests"]![0]!.AsObject().Remove("hasLong"); break;
            case "missingConfirmed": node["requests"]![0]!["instrument"]!["bars"]![0]!.AsObject().Remove("confirmed"); break;
            case "missingSettings": node["requests"]![0]!.AsObject().Remove("settings"); break;
            case "unknownJson": node["live"] = true; break;
            case "numericFleet": node["requests"]![0]!["fleet"] = 0; break;
            case "noRequests": node["requests"] = new JsonArray(); break;
        }
        File.WriteAllText(input, node.ToJsonString());
        if (fault == "duplicateJson") File.WriteAllText(input, File.ReadAllText(input).Replace("\"hasLong\":false", "\"hasLong\":false,\"hasLong\":true"));
        if (fault == "malformed") File.WriteAllText(input, "{SECRET_NEVER_LOG_THIS");
        var args = new List<string> { "--input", input, "--output", Output() };
        if (fault == "unknown") args.AddRange(new[] { "--live", "SECRET_NEVER_LOG_THIS" });
        if (fault == "duplicateArg") args.AddRange(new[] { "--input", input });
        if (fault == "missingValue") args.RemoveAt(args.Count - 1);
        var result = Run(args.ToArray());
        Assert.NotEqual(0, result.Code); Assert.NotEmpty(result.Stderr);
        Assert.DoesNotContain("SECRET_NEVER_LOG_THIS", result.Stderr);
        Assert.False(File.Exists(Output()));
    }

    [Theory]
    [InlineData("https://example.invalid/input.json")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//server/share/input.json")]
    [InlineData("missing.json")]
    public void CliRejectsRemoteInputAndMissingFile(string input)
    {
        var result = Run("--input", input, "--output", Output());
        Assert.NotEqual(0, result.Code); Assert.False(File.Exists(Output()));
    }

    [Fact]
    public void CliDeterministicAcrossRuns()
    {
        var input = Input();
        Assert.Equal(0, Run("--input", input, "--output", Output("first.json")).Code);
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
            Assert.Equal(0, Run("--input", input, "--output", Output("second.json")).Code);
        }
        finally { CultureInfo.CurrentCulture = prior; }
        Assert.Equal(File.ReadAllBytes(Output("first.json")), File.ReadAllBytes(Output("second.json")));
    }

    [Theory]
    [InlineData("zoneLessCutoff")]
    [InlineData("zoneLessBar")]
    [InlineData("nonUtcFuture")]
    public void CliRejectsAmbiguousTimestampSchema(string fault)
    {
        var input = Input(); var node = JsonNode.Parse(File.ReadAllText(input))!;
        if (fault == "zoneLessCutoff") node["requests"]![0]!["cutoffUtc"] = "2025-07-20T00:00:00";
        if (fault == "zoneLessBar") node["requests"]![0]!["instrument"]!["bars"]![0]!["openUtc"] = "2025-01-01T00:00:00";
        if (fault == "nonUtcFuture")
        {
            var bars = node["requests"]![0]!["instrument"]!["bars"]!.AsArray();
            var future = bars[199]!.DeepClone();
            future["openUtc"] = "2025-07-21T01:00:00+01:00";
            bars.Add(future);
        }
        File.WriteAllText(input, node.ToJsonString());
        Assert.NotEqual(0, Run("--input", input, "--output", Output()).Code);
        Assert.False(File.Exists(Output()));
    }

    [Fact]
    public void CliRejectsOutputOverwritingInput()
    {
        var input = Input(); var before = File.ReadAllBytes(input);
        Assert.NotEqual(0, Run("--input", input, "--output", input).Code);
        Assert.Equal(before, File.ReadAllBytes(input));
        Assert.NotEqual(0, Run("--input", input, "--output", Path.Combine(directory, ".", "input.json")).Code);
        Assert.Equal(before, File.ReadAllBytes(input));
    }

    [Fact]
    public void CliPreservesExistingOutputOnInvalidOrValidInput()
    {
        var input = Input(); File.WriteAllText(Output(), "KEEP");
        Assert.NotEqual(0, Run("--input", input, "--output", Output()).Code);
        Assert.Equal("KEEP", File.ReadAllText(Output()));
        File.WriteAllText(input, "{}");
        Assert.NotEqual(0, Run("--input", input, "--output", Output()).Code);
        Assert.Equal("KEEP", File.ReadAllText(Output()));
    }

    [Theory]
    [InlineData("config")]
    [InlineData("data")]
    [InlineData("cutoff")]
    public void CliRejectsInvalidConfigAndUsedDataWithoutPublishingPartialOutput(string fault)
    {
        var request = NavalD1EvaluatorTests.Request();
        var invalid = fault switch
        {
            "config" => request with { Settings = new NavalSettings(0, 30, 200) },
            "cutoff" => request with { CutoffUtc = request.CutoffUtc.ToOffset(TimeSpan.FromHours(1)) },
            _ => request with { Instrument = request.Instrument with { Bars = request.Instrument.Bars.Where((_, i) => i != 50).ToArray() } }
        };
        var result = Run("--input", Input(request, invalid), "--output", Output());
        Assert.NotEqual(0, result.Code); Assert.False(File.Exists(Output()));
        Assert.Equal(new[] { "input.json" }, Directory.GetFiles(directory).Select(Path.GetFileName));
    }

    [Fact]
    public void CliPublishesHonestWaitForMissingHistory()
    {
        var result = Run("--input", Input(NavalD1EvaluatorTests.Request() with { Btc = null }), "--output", Output());
        Assert.Equal(0, result.Code);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Output()));
        var d = doc.RootElement.GetProperty("decisions")[0];
        Assert.Equal("Wait", d.GetProperty("action").GetString());
        Assert.Equal("MissingBtcData", d.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("earliestExecutionUtc").ValueKind);
    }

    [Fact]
    public void CliRejectsSymbolicInputAndOutputAliases()
    {
        var input = Input();
        var alias = Output("alias.json"); File.CreateSymbolicLink(alias, input);
        Assert.NotEqual(0, Run("--input", alias, "--output", Output()).Code);
        Assert.NotEqual(0, Run("--input", input, "--output", alias).Code);
        Assert.False(File.Exists(Output()));
    }

    [Fact]
    public void NoExecutionRouteOrFinancialClaims()
    {
        var input = Input();
        Assert.Equal(0, Run("--input", input, "--output", Output()).Code);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Output()));
        var d = doc.RootElement.GetProperty("decisions")[0];
        Assert.Equal(new[] { "action", "asOfIdentity", "btcSma", "configIdentity", "cutoffUtc", "earliestExecutionUtc", "entryHigh", "exitLow", "fleet", "instrument", "reason", "stableId" },
            d.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        // API-level supplementary guard; executable tests above exercise real evaluator + file serializer.
        Assert.Equal(new[] { "Evaluate" }, typeof(NavalD1Evaluator).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Select(m => m.Name));
        Assert.Equal(new[] { "Wait", "Hold", "EntrySignal", "ExitSignal" }, Enum.GetNames<NavalAction>());
    }
}
