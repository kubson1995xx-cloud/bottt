using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Xunit;

namespace Apex.Tests;

public sealed class OkxAdvisoryFileTests
{
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal sealed class Reports:IDisposable
    {
        internal readonly string Root=Path.Combine("/home/ubuntu/apex-deliveries/okx-advisory-tests","okx-advisory-"+Guid.NewGuid().ToString("N"));
        internal Reports()
        {
            // The configured scratch ancestors are group-writable. Do not repair
            // them or weaken the native reader; use an admitted isolated test lane.
            Directory.CreateDirectory(Path.GetDirectoryName(Root)!,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            Directory.CreateDirectory(Root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.Combine(Root,"SOL-EUR"),UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            Put("vibe-trading.json",OkxAdvisoryTests.Report());Put("tradingagents.json",OkxAdvisoryTests.Report("tradingagents"));
        }
        internal void Put(string name,byte[] bytes)
        {string path=Path.Combine(Root,"SOL-EUR",name);File.WriteAllBytes(path,bytes);File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);}
        public void Dispose()=>Directory.Delete(Root,true);
    }
    internal static JsonElement Read(string root)
    {
        var type=typeof(OkxConfiguration).Assembly.GetType("Apex.Desktop.Okx.OkxAdvisoryReader");
        Assert.True(type!=null,"Missing bounded owned-root report reader");
        var method=type!.GetMethod("ReadForTests",BindingFlags.NonPublic|BindingFlags.Static);Assert.NotNull(method);
        var result=method!.Invoke(null,new object[]{root,OkxAdvisoryTests.Now});
        return JsonSerializer.SerializeToElement(result,result!.GetType());
    }
    [Fact]
    public void Reads_both_owned_fixed_filenames_and_never_changes_input_bytes()
    {
        if(!OperatingSystem.IsLinux())return;
        using var reports=new Reports();var before=Directory.GetFiles(reports.Root,"*",SearchOption.AllDirectories).ToDictionary(p=>Path.GetRelativePath(reports.Root,p),p=>File.ReadAllBytes(p));
        var result=Read(reports.Root);Assert.Equal("AGREE_BUY",result.GetProperty("Comparison").GetString());
        Assert.Equal(before.Count,Directory.GetFiles(reports.Root,"*",SearchOption.AllDirectories).Length);
        foreach(var pair in before)Assert.Equal(pair.Value,File.ReadAllBytes(Path.Combine(reports.Root,pair.Key!)));
        reports.Put("tradingagents.json",OkxAdvisoryTests.Report("tradingagents","SELL"));
        Assert.Equal("DISAGREE",Read(reports.Root).GetProperty("Comparison").GetString());
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("oversized")]
    [InlineData("symlink")]
    [InlineData("unsafe-file")]
    [InlineData("unsafe-root")]
    [InlineData("missing-root")]
    [InlineData("directory")]
    [InlineData("fifo")]
    [InlineData("hardlink")]
    public void Unsafe_or_absent_reports_are_bounded_unavailable_not_root_fallback(string mutation)
    {
        if(!OperatingSystem.IsLinux())return;
        using var reports=new Reports();string path=Path.Combine(reports.Root,"SOL-EUR","vibe-trading.json");string input=reports.Root;
        switch(mutation)
        {
            case "missing":File.Delete(path);break;
            case "oversized":reports.Put("vibe-trading.json",Encoding.UTF8.GetBytes(new string('x',16385)));break;
            case "symlink":File.Delete(path);File.CreateSymbolicLink(path,Path.Combine(reports.Root,"SOL-EUR","tradingagents.json"));break;
            case "unsafe-file":File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.OtherWrite);break;
            case "unsafe-root":File.SetUnixFileMode(reports.Root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupWrite);break;
            case "missing-root":input=Path.Combine(reports.Root,"absent");break;
            case "directory":File.Delete(path);Directory.CreateDirectory(path);break;
            case "fifo":File.Delete(path);Assert.Equal(0,MkFifo(path,0x180));break;
            case "hardlink":File.Delete(path);Assert.Equal(0,Link(Path.Combine(reports.Root,"SOL-EUR","tradingagents.json"),path));break;
        }
        var result=Read(input);Assert.Equal("UNAVAILABLE",result.GetProperty("Comparison").GetString());
        Assert.Equal("REPORT_FILES_UNAVAILABLE",result.GetProperty("Reason").GetString());
        Assert.False(result.GetProperty("ExecutionAllowed").GetBoolean());
    }
    [Fact]
    public void Friend_reader_refuses_non_fixture_roots_before_any_open()
    {
        var result=Read("/not-an-admitted-test-root");
        Assert.Equal("TEST_ROOT_REFUSED",result.GetProperty("Reason").GetString());
    }
    [System.Runtime.InteropServices.DllImport("libc",EntryPoint="mkfifo")]private static extern int MkFifo(string path,uint mode);
    [System.Runtime.InteropServices.DllImport("libc",EntryPoint="link")]private static extern int Link(string oldPath,string newPath);

    [Fact]
    public async Task Host_friend_default_never_falls_through_to_real_report_root()
    {
        if(!OperatingSystem.IsLinux())return;
        using var reports=new Reports();using var output=new StringWriter();
        int code=await LinuxDemoHost.RunForTestsAsync(reports.Root,new LinuxDemoCredentialVault(Path.Combine(reports.Root,"nonexistent-vault")),
            new StringReader("ADVISORY-STATUS\nQUIT\n"),output,(_,_)=>throw new Exception("NO_DISCOVERY"),null,CancellationToken.None);
        Assert.Equal(0,code);Assert.Contains("NOT_CONFIGURED",output.ToString());Assert.False(Directory.Exists(Path.Combine(reports.Root,"nonexistent-vault")));
    }

    [Fact]
    public async Task Active_scheduler_refuses_advisory_before_reading_reports_or_changing_authority()
    {
        if(!OperatingSystem.IsLinux())return;
        using var reports=new Reports();using var output=new StringWriter();using var input=new StringReader("ADVISORY-STATUS\nQUIT\n");
        var type=typeof(LinuxDemoHost);
        var host=type.GetConstructors(BindingFlags.NonPublic|BindingFlags.Instance).Single().Invoke(new object?[]{reports.Root,null,
            (Func<CancellationToken,ValueTask<string?>>)(ct=>new ValueTask<string?>(input.ReadLineAsync(ct).AsTask())),output,null,null,(Func<bool>)(()=>true),CancellationToken.None});
        type.GetField("scheduler",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(host,Task.CompletedTask);
        type.GetField("advisory",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(host,(Func<string,OkxAdvisoryStatus>)(_=>throw new Exception("REPORT_READ_FORBIDDEN_WHILE_SCHEDULED")));
        int code=await (Task<int>)type.GetMethod("RunCoreAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(host,null)!;
        Assert.Equal(0,code);Assert.Contains("ADVISORY_REFUSED_STOP_UNARMED_REQUIRED",output.ToString());Assert.DoesNotContain("ADVISORY_STATUS ",output.ToString());
    }

    [Fact]
    public async Task Advisory_friend_rejects_unowned_roots_and_authority_commands_before_host_dispatch()
    {
        var method=typeof(LinuxDemoHost).GetMethod("RunAdvisoryForTestsAsync",BindingFlags.NonPublic|BindingFlags.Static);Assert.NotNull(method);
        using var output=new StringWriter();
        int code=await (Task<int>)method!.Invoke(null,new object[]{"/not-an-admitted-test-root",new StringReader("CONNECT\nQUIT\n"),output,OkxAdvisoryTests.Now,CancellationToken.None})!;
        Assert.Equal(2,code);Assert.Contains("ADVISORY_TEST_ROOT_REFUSED",output.ToString());
        if(!OperatingSystem.IsLinux())return;
        using var reports=new Reports();using var output2=new StringWriter();
        code=await (Task<int>)method.Invoke(null,new object[]{reports.Root,new StringReader("CONNECT\nARM\nSTART\nQUIT\n"),output2,OkxAdvisoryTests.Now,CancellationToken.None})!;
        Assert.Equal(0,code);Assert.Equal(3,output2.ToString().Split('\n').Count(l=>l.TrimEnd('\r')=="ADVISORY_TEST_COMMAND_REFUSED"));
        Assert.DoesNotContain("CONNECTED_STOP",output2.ToString());Assert.False(File.Exists(Path.Combine(reports.Root,".run.lock")));
    }
    [Fact]
    public async Task Actual_native_host_advisory_command_reads_synthetic_reports_without_vault_network_or_financial_writes()
    {
        if(!OperatingSystem.IsLinux())return;
        using var reports=new Reports();var before=Directory.GetFiles(reports.Root,"*",SearchOption.AllDirectories).ToDictionary(p=>Path.GetRelativePath(reports.Root,p),p=>File.ReadAllBytes(p));
        var method=typeof(LinuxDemoHost).GetMethod("RunAdvisoryForTestsAsync",BindingFlags.NonPublic|BindingFlags.Static);
        Assert.True(method!=null,"Distinct isolated advisory host seam required; never call production Main");
        using var output=new StringWriter();
        int code=await (Task<int>)method!.Invoke(null,new object[]{reports.Root,new StringReader("ADVISORY-STATUS\nSTATUS\nQUIT\n"),output,OkxAdvisoryTests.Now,CancellationToken.None})!;
        Assert.Equal(0,code);string text=output.ToString();
        var line=text.Split('\n').Single(l=>l.StartsWith("ADVISORY_STATUS ",StringComparison.Ordinal));
        var status=JsonDocument.Parse(line.Substring("ADVISORY_STATUS ".Length)).RootElement;
        Assert.Equal("AGREE_BUY",status.GetProperty("Comparison").GetString());
        Assert.False(status.GetProperty("AutomaticActivation").GetBoolean());Assert.False(status.GetProperty("ExecutionAllowed").GetBoolean());
        Assert.Contains("STOP_DISCONNECTED_UNARMED",text);Assert.Contains("HOST_DRAINED_CREDENTIALS_CLOSED",text);
        Assert.DoesNotContain("Synthetic public research",text);Assert.DoesNotContain("https://",text);
        Assert.Equal(before.Count,Directory.GetFiles(reports.Root,"*",SearchOption.AllDirectories).Length);
        foreach(var pair in before)Assert.Equal(pair.Value,File.ReadAllBytes(Path.Combine(reports.Root,pair.Key!)));
    }
}
