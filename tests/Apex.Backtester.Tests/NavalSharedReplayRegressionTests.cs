using System.Text.Json.Nodes;
using Apex.Backtester;
using Xunit;

namespace Apex.Backtester.Tests;

public class NavalSharedReplayRegressionTests
{
    static string Fixture => Path.Combine(Program.FindRepoRoot(), "tests/Apex.Backtester.Tests/fixtures/synthetic/naval-shared-replay-v1.json");
    static string Scratch() => Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "shared-regression-" + Guid.NewGuid().ToString("N"));
    static int Run(string input, string state, string output) => Program.Main(new[] { "naval-shared-replay", "--input", input, "--state-dir", state, "--output", output });
    static void Input(string path, string steps)
    {
        var node = JsonNode.Parse(File.ReadAllText(Fixture))!;
        node["steps"] = JsonNode.Parse(steps);
        File.WriteAllText(path, node.ToJsonString());
    }

    // Returning nonzero after replay has committed is insufficient: every authority byte must be unchanged.
    [Theory]
    [InlineData(1, "authority")] [InlineData(2, "authority")]
    [InlineData(1, "nested")] [InlineData(2, "nested")]
    [InlineData(1, "directory")] [InlineData(1, "input")]
    public void NavalSharedReplay_PathContainment_RefusesBeforeAuthorityMutation(int separators, string target)
    {
        string dir = Scratch(); Directory.CreateDirectory(dir);
        try
        {
            string state = Path.Combine(dir, "state"), outside = Path.Combine(dir, "result.json");
            Assert.Equal(0, Run(Fixture, state, outside));
            string authority = Path.Combine(state, "naval-authority-v1.json"), input = Path.Combine(dir, "input.json");
            Input(input, """[{"op":"reconcile"},{"op":"start"}]""");
            var before = Directory.GetFiles(state, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
            string output = target == "authority" ? authority : target == "directory" ? state : Path.Combine(state, "nested", "report.json");
            int code = Run(target == "input" ? authority : input, state + new string(Path.DirectorySeparatorChar, separators), target == "input" ? outside : output);
            foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
            Assert.Equal(before.Keys.OrderBy(p => p), Directory.GetFiles(state, "*", SearchOption.AllDirectories).OrderBy(p => p));
            Assert.Equal(2, code);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory] [InlineData(1)] [InlineData(2)]
    public void NavalSharedReplay_PathContainment_RefusesBeforeCreatingNewAuthority(int separators)
    {
        string dir = Scratch(); Directory.CreateDirectory(dir);
        try
        {
            string state = Path.Combine(dir, "state");
            Assert.Equal(2, Run(Fixture, state + new string(Path.DirectorySeparatorChar, separators), Path.Combine(state, "naval-authority-v1.json")));
            Assert.False(Directory.Exists(state));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NavalSharedReplay_PathContainment_FilesystemRootIsNotTrimmedToEmpty()
    {
        string dir = Scratch(); Directory.CreateDirectory(dir);
        var previous = Console.Error; using var error = new StringWriter();
        try
        {
            // Missing input makes this safe even with a broken guard: no root owner/store can be opened.
            Console.SetError(error);
            Assert.Equal(2, Run(Path.Combine(dir, "missing.json"), Path.GetPathRoot(dir)!, Path.Combine(dir, "out.json")));
            Assert.Contains("Input/output must be separate from authority directory", error.ToString());
            Assert.False(File.Exists(Path.Combine(dir, "out.json")));
        }
        finally { Console.SetError(previous); Directory.Delete(dir, true); }
    }

    [Fact]
    public void NavalSharedReplay_PathContainment_UsesNativeCaseIdentityAndComponentBoundaries()
    {
        string dir = Scratch(); Directory.CreateDirectory(dir);
        try
        {
            string state = Path.Combine(dir, "state"), authority = Path.Combine(state, "naval-authority-v1.json"), input = Path.Combine(dir, "input.json");
            Assert.Equal(0, Run(Fixture, state, Path.Combine(dir, "result.json")));
            byte[] before = File.ReadAllBytes(authority);
            Input(input, "[]");
            // Native Windows aliases must refuse; Linux case-distinct directories are not aliases.
            string aliasOutput = Path.Combine(dir, "STATE", "NAVAL-AUTHORITY-V1.JSON");
            Assert.Equal(OperatingSystem.IsWindows() ? 2 : 0, Run(input, state + Path.DirectorySeparatorChar, aliasOutput));
            Assert.Equal(before, File.ReadAllBytes(authority));
            string siblingOutput = Path.Combine(dir, "state-sibling", "result.json");
            Assert.Equal(0, Run(input, state + Path.DirectorySeparatorChar, siblingOutput));
            Assert.Equal(before, File.ReadAllBytes(authority));
            // File identity is also OS-sensitive, not just the authority containment check.
            string aliasInput = Path.Combine(dir, "INPUT.JSON");
            Assert.Equal(OperatingSystem.IsWindows() ? 2 : 0, Run(input, state, aliasInput));
            Assert.Equal(before, File.ReadAllBytes(authority));
        }
        finally { Directory.Delete(dir, true); }
    }
}
