using System.Threading.Tasks;

namespace Apex.Tests;

// Test executable only; not packaged. The production executable always calls Run(..., false).
// Process-based synthetic trading tests need composition authority, never a JSON/CLI bypass.
internal static class SyntheticHostEntry
{
    public static Task<int> Main(string[] args) => Apex.Desktop.Program.Run(args, trustedSyntheticComposition: true);
}
