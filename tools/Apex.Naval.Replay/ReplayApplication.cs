using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apex.Core.Naval;

namespace Apex.Naval.Replay;

/// <summary>Local snapshot replay only. No network, broker composition or execution authority.</summary>
public sealed class ReplayApplication
{
    private const int MaxInputBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? temporary = null;
        try
        {
            if (args.Length != 4) throw new ReplayException("arguments: use --input <local.json> --output <new-local.json>");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2)
            {
                if ((args[i] != "--input" && args[i] != "--output") || !options.TryAdd(args[i], args[i + 1]))
                    throw new ReplayException("arguments: unknown or duplicate option");
            }
            if (!options.ContainsKey("--input") || !options.ContainsKey("--output")) throw new ReplayException("arguments: missing option");
            var input = LocalPath(options["--input"]);
            var destination = LocalPath(options["--output"]);
            if (string.Equals(input, destination, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ReplayException("output: cannot overwrite input");
            if (!File.Exists(input)) throw new ReplayException("input: local file not found");
            if (File.Exists(destination) || Directory.Exists(destination)) throw new ReplayException("output: target already exists");
            var parent = Path.GetDirectoryName(destination)!;
            if (!Directory.Exists(parent)) throw new ReplayException("output: parent directory must exist");

            // One bounded byte snapshot is both parsed and hashed. No second input read.
            byte[] snapshot = ReadSnapshot(input);
            var inputHash = Convert.ToHexString(SHA256.HashData(snapshot)).ToLowerInvariant();
            using var document = JsonDocument.Parse(snapshot, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            Object(root, "schemaVersion", "datasetLabel", "requests");
            if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.String
                || root.GetProperty("schemaVersion").GetString() != "naval-replay-v1") throw new ReplayException("schema: unsupported version");
            Text(root.GetProperty("datasetLabel"));
            var requests = root.GetProperty("requests");
            if (requests.ValueKind != JsonValueKind.Array || requests.GetArrayLength() is < 1 or > 1000)
                throw new ReplayException("schema: requests must contain 1..1000 items");
            var evaluator = new NavalD1Evaluator();
            var decisions = new List<NavalDecision>();
            foreach (var element in requests.EnumerateArray())
            {
                Request(element);
                var request = element.Deserialize<NavalEvaluationRequest>(Json)!;
                var decision = evaluator.Evaluate(request);
                if (decision.Reason.StartsWith("Invalid", StringComparison.Ordinal))
                    throw new ReplayException("data/config: evaluator rejected request " + decisions.Count);
                decisions.Add(decision);
            }
            var result = new
            {
                schemaVersion = "naval-replay-result-v1", mode = "OFFLINE", execution = "NO_EXECUTION",
                datasetLabel = root.GetProperty("datasetLabel").GetString(), inputSha256 = inputHash,
                rulesVersion = NavalD1Evaluator.RuleVersion, decisions
            };
            byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(result, Json);
            temporary = Path.Combine(parent, ".naval-" + Path.GetRandomFileName());
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(encoded); file.Flush(flushToDisk: true);
            }
            // Same-directory rename; refuse all existing output, including input hard-link aliases.
            File.Move(temporary, destination, overwrite: false);
            temporary = null;
            output.WriteLine("OFFLINE / NO_EXECUTION: " + decisions.Count + " decisions; input SHA256 " + inputHash);
            return 0;
        }
        catch (ReplayException exception)
        {
            error.WriteLine("NAVAL_ERROR: " + exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException
            or IOException or UnauthorizedAccessException or NotSupportedException or OverflowException)
        {
            // Do not disclose arbitrary JSON, filenames, secrets or framework exception payloads.
            error.WriteLine("NAVAL_ERROR: invalid schema, local file or output; no replay published");
            return 2;
        }
        finally
        {
            if (temporary != null)
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains("://", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ReplayException("path: local filesystem paths only, no URI or network share");
        int colon = path.IndexOf(':');
        if (colon >= 0 && !(OperatingSystem.IsWindows() && colon == 1 && char.IsLetter(path[0]) && path.LastIndexOf(':') == 1))
            throw new ReplayException("path: URI and stream paths are not supported");
        var full = Path.GetFullPath(path);
        // Conservative alias protection. Trust the local directory against concurrent hostile replacement.
        FileSystemInfo? current = new FileInfo(full);
        while (current != null)
        {
            if (current.LinkTarget != null || (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new ReplayException("path: symbolic/reparse aliases are not supported");
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
        return full;
    }

    private static byte[] ReadSnapshot(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxInputBytes) throw new ReplayException("input: snapshot exceeds 32 MiB limit");
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (bytes.Length + read > MaxInputBytes) throw new ReplayException("input: snapshot exceeds 32 MiB limit");
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    private static void Object(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ReplayException("schema: expected object");
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw new ReplayException("schema: unknown or duplicate property");
        if (remaining.Count != 0) throw new ReplayException("schema: missing required property");
    }

    private static void Text(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString())
            || element.GetString()!.Length > 256 || element.GetString()!.Any(char.IsControl))
            throw new ReplayException("schema: nonempty text of at most 256 characters required");
    }

    private static void Request(JsonElement element)
    {
        Object(element, "fleet", "instrument", "btc", "cutoffUtc", "hasLong", "settings");
        var fleet = element.GetProperty("fleet");
        if (fleet.ValueKind != JsonValueKind.String || fleet.GetString() is not ("Alpha" or "Bravo"))
            throw new ReplayException("schema: fleet must be Alpha or Bravo");
        Series(element.GetProperty("instrument"));
        if (element.GetProperty("btc").ValueKind != JsonValueKind.Null) Series(element.GetProperty("btc"));
        Timestamp(element.GetProperty("cutoffUtc"));
        Boolean(element.GetProperty("hasLong"));
        var settings = element.GetProperty("settings");
        Object(settings, "entryDays", "exitDays", "btcSmaDays");
        foreach (var property in settings.EnumerateObject())
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out _))
                throw new ReplayException("schema: integer lookbacks required");
    }

    private static void Series(JsonElement element)
    {
        Object(element, "instrument", "baseAsset", "quoteAsset", "source", "priceBasis", "bars");
        foreach (var name in new[] { "instrument", "baseAsset", "quoteAsset", "source", "priceBasis" }) Text(element.GetProperty(name));
        var bars = element.GetProperty("bars");
        if (bars.ValueKind != JsonValueKind.Array || bars.GetArrayLength() > 100000) throw new ReplayException("schema: bars array required (max 100000)");
        foreach (var bar in bars.EnumerateArray())
        {
            Object(bar, "openUtc", "open", "high", "low", "close", "volume", "confirmed");
            Timestamp(bar.GetProperty("openUtc"));
            foreach (var name in new[] { "open", "high", "low", "close", "volume" })
                if (bar.GetProperty(name).ValueKind != JsonValueKind.Number || !bar.GetProperty(name).TryGetDecimal(out _))
                    throw new ReplayException("schema: decimal OHLCV numbers required");
            Boolean(bar.GetProperty("confirmed"));
        }
    }

    private static void Timestamp(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) throw new ReplayException("schema: UTC timestamp required");
        var text = element.GetString()!;
        if (!(text.EndsWith("Z", StringComparison.Ordinal) || text.EndsWith("+00:00", StringComparison.Ordinal))
            || !element.TryGetDateTimeOffset(out var timestamp) || timestamp.Offset != TimeSpan.Zero)
            throw new ReplayException("schema: explicit UTC Z or +00:00 timestamp required");
    }

    private static void Boolean(JsonElement element)
    {
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ReplayException("schema: boolean required");
    }

    private sealed class ReplayException(string message) : Exception(message);
}
