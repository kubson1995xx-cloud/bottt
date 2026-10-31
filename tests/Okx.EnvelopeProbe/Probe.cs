using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Apex.Desktop.Okx;

// Test-only allocation observer: retains actual backing-array references, not
// plaintext copies. Normal writes delegate unchanged. Faults occur AFTER each
// outer write, or immediately before ToArray, so cleanup sees real partial data.
internal sealed class TrackingMemoryStream : MemoryStream
{
    internal static readonly List<TrackingMemoryStream> Instances = new();
    internal static int FailAfterWrite;
    internal static bool FailAtOutput;
    internal readonly List<byte[]> Buffers = new();
    internal int Writes;
    private int depth;
    internal TrackingMemoryStream() { Instances.Add(this); Remember(); }
    internal TrackingMemoryStream(byte[] b, int i, int n, bool writable, bool publiclyVisible) : base(b, i, n, writable, publiclyVisible) { Instances.Add(this); Remember(); }
    private void Remember() { var b = GetBuffer(); if (!Buffers.Any(x => ReferenceEquals(x, b))) Buffers.Add(b); }
    private void Before() { depth++; Remember(); }
    private void After()
    {
        Remember();
        if (--depth == 0 && ++Writes == FailAfterWrite) throw new InjectedEncodeFailure();
    }
    public override void Write(byte[] b, int o, int n) { Before(); try { base.Write(b, o, n); } finally { After(); } }
    public override void Write(ReadOnlySpan<byte> b) { Before(); try { base.Write(b); } finally { After(); } }
    public override void WriteByte(byte b) { Before(); try { base.WriteByte(b); } finally { After(); } }
    public override byte[] ToArray() { if (FailAtOutput) throw new InjectedEncodeFailure(); return base.ToArray(); }
}
internal sealed class InjectedEncodeFailure : IOException { }
internal static class Probe
{
    private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    internal static int Main(string[] args)
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(args[1]));
        var fixture = fixtures.RootElement;
        var scope = fixture.GetProperty("scope");
        var options = new OkxNavalOptions { Schema = scope.GetProperty("Schema").GetString(), Scopes = new[] { new OkxScopeOptions {
            ScopeId = scope.GetProperty("ScopeId").GetString(), Environment = scope.GetProperty("Environment").GetString(),
            Region = scope.GetProperty("Region").GetString(), ExpectedUid = scope.GetProperty("ExpectedUid").GetString(),
            ExpectedMainUid = scope.GetProperty("ExpectedMainUid").GetString(), QuoteCurrency = scope.GetProperty("QuoteCurrency").GetString(),
            CredentialBindingId = scope.GetProperty("CredentialBindingId").GetString(), SafetyVersion = scope.GetProperty("SafetyVersion").GetString() } } };
        var binding = OkxConfiguration.Bind(options)[0];
        var product = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        Type ProductType(string n) => product.GetType("Apex.Desktop.Okx." + n, true)!;
        var productOptions = JsonSerializer.Deserialize(JsonSerializer.Serialize(options), ProductType("OkxNavalOptions"));
        var productBinding = ((System.Collections.IEnumerable)ProductType("OkxConfiguration").GetMethod("Bind", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new[] { productOptions })!).Cast<object>().Single();
        var encode = ProductType("OkxCredentialEnvelope").GetMethod("Encode", BindingFlags.NonPublic | BindingFlags.Static)!;
        var decode = ProductType("OkxCredentialEnvelope").GetMethod("Decode", BindingFlags.NonPublic | BindingFlags.Static)!;
        var results = new List<object>();
        bool passed = true;
        foreach (var sizes in fixture.GetProperty("sizes").EnumerateArray())
        {
            int[] lengths = sizes.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            byte[][] fields = lengths.Select(RandomNumberGenerator.GetBytes).ToArray();
            byte[]? expected = null;
            try
            {
                using var productLease = (IDisposable)Activator.CreateInstance(ProductType("OkxExecutionCredentialLease"), BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new object[] { productBinding, "DemoTradingApi", fields[0].ToArray(), fields[1].ToArray(), fields[2].ToArray() }, null)!;
                expected = (byte[])encode.Invoke(null, new object[] { productLease })!;
                int writeCount = 0;
                // Success first discovers actual write boundaries; then every
                // partial-write exit and the output-allocation exit is tested.
                for (int fault = 0; fault <= writeCount + 1; fault++)
                {
                    TrackingMemoryStream.Instances.Clear();
                    TrackingMemoryStream.FailAfterWrite = fault;
                    TrackingMemoryStream.FailAtOutput = fault != 0 && fault == writeCount + 1;
                    using var lease = new OkxExecutionCredentialLease(binding, "DemoTradingApi", fields[0].ToArray(), fields[1].ToArray(), fields[2].ToArray());
                    byte[]? output = null;
                    // Keep references only, so disposal is checked against every
                    // owned lease field on successful and injected-error exits.
                    var leaseFields = new[] { "key", "secret", "passphrase" }.Select(n => (byte[])typeof(OkxExecutionCredentialLease).GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lease)!).ToArray();
                    bool injected = false, parity = false, roundTrip = false, legacyDecode = false;
                    int envelopeLength = 0;
                    try
                    {
                        output = OkxCredentialEnvelope.Encode(lease);
                        envelopeLength = output.Length;
                        Require(fault == 0, "Expected injected failure was not reached");
                        parity = output.AsSpan().SequenceEqual(expected);
                        using var opened = OkxCredentialEnvelope.Decode(output, binding);
                        roundTrip = opened.ApiKey.SequenceEqual(fields[0]) && opened.Secret.SequenceEqual(fields[1]) && opened.Passphrase.SequenceEqual(fields[2]);
                        using var productOpened = (IDisposable)decode.Invoke(null, new object[] { output, productBinding })!;
                        legacyDecode = new[] { "key", "secret", "passphrase" }.Select((n, i) => ((byte[])ProductType("OkxExecutionCredentialLease").GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(productOpened)!).AsSpan().SequenceEqual(fields[i])).All(x => x);
                        Require(parity && roundTrip && legacyDecode, "Production output/roundtrip compatibility control failed");
                    }
                    catch (InjectedEncodeFailure) { injected = true; Require(fault != 0, "Unexpected success-path fault"); }
                    finally { lease.Dispose(); if (output != null) CryptographicOperations.ZeroMemory(output); }
                    var stream = TrackingMemoryStream.Instances.Single();
                    if (fault == 0) writeCount = stream.Writes;
                    bool allCleared = stream.Buffers.All(b => b.All(v => v == 0));
                    bool leaseFieldsCleared = leaseFields.All(b => b.All(v => v == 0));
                    bool outputCleared = output == null || output.All(v => v == 0);
                    bool containsKey = stream.Buffers.Any(b => b.AsSpan().IndexOf(fields[0]) >= 0);
                    bool containsSecret = stream.Buffers.Any(b => b.AsSpan().IndexOf(fields[1]) >= 0);
                    bool containsPassphrase = stream.Buffers.Any(b => b.AsSpan().IndexOf(fields[2]) >= 0);
                    passed &= allCleared && leaseFieldsCleared && outputCleared;
                    results.Add(new { lengths, fault, outputFailure = TrackingMemoryStream.FailAtOutput, injected, productionOutputEqual = parity, roundTrip, comparisonDecoderRoundTrip = legacyDecode,
                        envelopeLength, leaseFieldsCleared, outputCleared, backingBufferSizes = stream.Buffers.Select(b => b.Length).ToArray(), allTemporaryBuffersCleared = allCleared,
                        containsKey, containsSecret, containsPassphrase, finalBackingBufferCleared = stream.Buffers.Last().All(b => b == 0) });
                    foreach (var b in stream.Buffers) CryptographicOperations.ZeroMemory(b);
                }
            }
            finally { if (expected != null) CryptographicOperations.ZeroMemory(expected); foreach (var f in fields) CryptographicOperations.ZeroMemory(f); }
        }
        File.WriteAllText(args[2], JsonSerializer.Serialize(new { outcome = passed ? "PASS" : "FAIL", results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Buffer cleanup oracle: {(passed ? "PASS" : "FAIL")}; cases={results.Count}");
        return passed ? 0 : 1;
    }
}
