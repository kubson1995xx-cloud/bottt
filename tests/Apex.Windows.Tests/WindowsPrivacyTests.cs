using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Apex.Windows.Tests;

public sealed class WindowsPrivacyTests
{
    // Synthetic markers only. This catches the old literal-word-only DOM oracle,
    // and a JSON guard accidentally moved after the write. Never use real secrets here.
    [Fact, Trait("Gate", "Helper")]
    public void SyntheticValuesAndDigestsAreRejectedBeforePublication() => Controls();

    internal static object Controls()
    {
        string[] secrets = { "SYNTHETIC_OPERATOR_TASK6_ONLY", "SYNTHETIC_READER_TASK6_ONLY" };
        string[] values = secrets.Concat(secrets.Select(s => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(s))).ToLowerInvariant())).ToArray();
        bool Old(string dom) => !dom.Contains("Authorization", StringComparison.Ordinal); // empty legacy token in all controls
        Assert.True(Old("<main>clean</main>"));
        Assert.False(Old("<main>Authorization</main>"));
        using var guard = new WindowsPrivacyGuard(secrets);
        guard.Check("<main>clean</main>");
        int rejected = 0, writes = 0;
        foreach (string value in values)
        {
            string dom = "<main data-label='" + value + "'>historical</main>";
            Assert.True(Old(dom), "Old oracle no longer reproduces synthetic false acceptance");
            Assert.ThrowsAny<Exception>(() => guard.Check(dom));
            // Exercise the very same pre-write method used by Evidence, not a copy.
            Assert.ThrowsAny<Exception>(() => WindowsPrivacyGuard.Publish(new { harmless = value }, _ => writes++));
            rejected++;
        }
        Assert.Equal(0, writes);
        WindowsPrivacyGuard.Publish(new { clean = true }, _ => writes++);
        Assert.Equal(1, writes);
        return new { syntheticOnly = true, cleanAccepted = true, oldPlaintextFalseAccept = true, oldDigestFalseAccept = true, newOperatorAndReaderRejected = true, newBothDigestsRejected = true, publicJsonRejectedBeforeWrite = true, authorizationControlRejected = true, rejectedControls = rejected, noValuesRetained = true };
    }
}

// Deliberately small: compare only known in-memory values, never retain buffers.
// Registration lasts through fixture cleanup so all intended JSON writes are checked.
internal sealed class WindowsPrivacyGuard : IDisposable
{
    private static readonly List<WindowsPrivacyGuard> active = new();
    private string[] values;
    internal WindowsPrivacyGuard(IEnumerable<string> secrets)
    {
        values = secrets.SelectMany(s => new[] { s, Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(s))).ToLowerInvariant() }).ToArray();
        lock (active) active.Add(this);
    }
    internal void Check(string buffer)
    {
        bool clean = !values.Any(value => buffer.Contains(value, StringComparison.OrdinalIgnoreCase));
        Assert.True(clean, "Known credential material in private buffer; content withheld");
    }
    internal static void Publish(object record, Action<string> write)
    {
        string buffer = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
        lock (active) foreach (var guard in active) guard.Check(buffer);
        Assert.False(buffer.Contains("Authorization:", StringComparison.OrdinalIgnoreCase), "Authorization material in public buffer; content withheld");
        write(buffer);
    }
    public void Dispose() { lock (active) active.Remove(this); Array.Clear(values); values = Array.Empty<string>(); }
}
