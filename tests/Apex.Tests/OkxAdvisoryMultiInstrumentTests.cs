using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Xunit;

namespace Apex.Tests;

public sealed class OkxAdvisoryMultiInstrumentTests
{
    private static byte[] Report(string instrument,string tool="vibe-trading",string recommendation="BUY") =>
        OkxAdvisoryTests.Report(tool,recommendation,f=>f["Instrument"]=instrument);
    private static JsonElement Compare(string requested,byte[] left,byte[] right)
    {
        var type=typeof(OkxConfiguration).Assembly.GetType("Apex.Desktop.Okx.OkxAdvisoryComparison")!;
        var method=type.GetMethod("CompareForInstrument",BindingFlags.Static|BindingFlags.NonPublic);
        Assert.True(method!=null,"Exact approved instrument argument is required, with no arbitrary universe");
        var result=method!.Invoke(null,new object[]{left,right,OkxAdvisoryTests.Now,requested});
        return JsonSerializer.SerializeToElement(result,result!.GetType());
    }
    [Theory]
    [InlineData("BTC-EUR")]
    [InlineData("ETH-EUR")]
    [InlineData("SOL-EUR")]
    public void Approved_exact_instrument_pair_retains_daily_shadow_and_no_financial_authority(string instrument)
    {
        var status=Compare(instrument,Report(instrument),Report(instrument,"tradingagents"));
        Assert.Equal(instrument,status.GetProperty("Instrument").GetString());
        Assert.Equal("AGREE_BUY",status.GetProperty("Comparison").GetString());
        Assert.False(status.GetProperty("AutomaticActivation").GetBoolean());
        Assert.False(status.GetProperty("ExecutionAllowed").GetBoolean());
        Assert.False(status.GetProperty("BlocksMandatoryExit").GetBoolean());
        Assert.Equal("SHADOW_ONLY",status.GetProperty("Mode").GetString());
    }
    [Theory]
    [InlineData("BTC-EUR","ETH-EUR")]
    [InlineData("ETH-EUR","SOL-EUR")]
    [InlineData("SOL-EUR","BTC-EUR")]
    [InlineData("BTC-EUR","BTC-USDT")]
    [InlineData("ETH-EUR","eth-eur")]
    [InlineData("SOL-EUR","SOL-USD")]
    public void Both_reports_must_match_requested_instrument_not_merely_each_other(string requested,string wrong)
    {
        foreach(bool badLeft in new[]{true,false})
        {
            var status=Compare(requested,Report(badLeft?wrong:requested),Report(badLeft?requested:wrong,"tradingagents"));
            Assert.Equal("UNAVAILABLE",status.GetProperty("Comparison").GetString());
            Assert.Equal(badLeft?"INVALID_VIBE":"INVALID_TRADINGAGENTS",status.GetProperty("Reason").GetString());
        }
        Assert.Equal("UNAVAILABLE",Compare(requested,Report(wrong),Report(wrong,"tradingagents")).GetProperty("Comparison").GetString());
    }
    [Theory]
    [InlineData("")]
    [InlineData("btc-eur")]
    [InlineData("BTC/EUR")]
    [InlineData("BTC-USDT")]
    [InlineData("XRP-EUR")]
    [InlineData(" SOL-EUR")]
    [InlineData("SOL-EUR ")]
    [InlineData("../SOL-EUR")]
    [InlineData("SOL-EUR/../../BTC-EUR")]
    [InlineData("/SOL-EUR")]
    public void Unknown_alias_or_path_token_refuses_before_parsing_or_file_access(string requested)
    {
        var status=Compare(requested,Array.Empty<byte>(),Array.Empty<byte>());
        Assert.Equal("INVALID_INSTRUMENT",status.GetProperty("Reason").GetString());
        Assert.Equal("",status.GetProperty("Instrument").GetString());
        var reader=typeof(OkxConfiguration).Assembly.GetType("Apex.Desktop.Okx.OkxAdvisoryReader")!;
        var method=reader.GetMethod("ReadInstrumentForTests",BindingFlags.Static|BindingFlags.NonPublic);Assert.NotNull(method);
        // Admitted prefix but absent root: instrument rejection must precede any open.
        var result=method!.Invoke(null,new object[]{"/home/ubuntu/apex-deliveries/okx-advisory-tests/okx-advisory-absent",requested,OkxAdvisoryTests.Now});
        var fileStatus=JsonSerializer.SerializeToElement(result,result!.GetType());
        Assert.Equal("INVALID_INSTRUMENT",fileStatus.GetProperty("Reason").GetString());
    }
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private sealed class Reports:IDisposable
    {
        internal readonly string Root=Path.Combine("/home/ubuntu/apex-deliveries/okx-advisory-tests","okx-advisory-"+Guid.NewGuid().ToString("N"));
        internal Reports()
        {
            Directory.CreateDirectory(Root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            foreach(string instrument in new[]{"BTC-EUR","ETH-EUR","SOL-EUR"})
            {
                var dir=Path.Combine(Root,instrument);Directory.CreateDirectory(dir,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
                string recommendation=instrument=="BTC-EUR"?"BUY":instrument=="ETH-EUR"?"WAIT":"SELL";
                foreach(string tool in new[]{"vibe-trading","tradingagents"})
                {var path=Path.Combine(dir,tool+".json");File.WriteAllBytes(path,Report(instrument,tool,recommendation));File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);}
            }
        }
        public void Dispose()=>Directory.Delete(Root,true);
    }
    [Fact]
    public async Task Exact_host_commands_select_fixed_instrument_subdirectories_without_writes_or_fallback()
    {
        if(!OperatingSystem.IsLinux())return;
        using var reports=new Reports();using var output=new StringWriter();
        var before=Directory.GetFiles(reports.Root,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>File.ReadAllBytes(p));
        int code=await LinuxDemoHost.RunAdvisoryForTestsAsync(reports.Root,
            new StringReader("ADVISORY-STATUS BTC-EUR\nADVISORY-STATUS ETH-EUR\nADVISORY-STATUS SOL-EUR\nADVISORY-STATUS\nQUIT\n"),output,OkxAdvisoryTests.Now,CancellationToken.None);
        Assert.Equal(0,code);
        var rows=output.ToString().Split('\n').Where(x=>x.StartsWith("ADVISORY_STATUS ",StringComparison.Ordinal)).Select(x=>JsonDocument.Parse(x.Substring(16)).RootElement.Clone()).ToArray();
        Assert.Equal(4,rows.Length);
        Assert.Equal(new[]{"BTC-EUR","ETH-EUR","SOL-EUR","SOL-EUR"},rows.Select(x=>x.GetProperty("Instrument").GetString()).ToArray());
        Assert.Equal(new[]{"AGREE_BUY","AGREE_WAIT","AGREE_SELL","AGREE_SELL"},rows.Select(x=>x.GetProperty("Comparison").GetString()).ToArray());
        Assert.All(rows,x=>{Assert.False(x.GetProperty("ExecutionAllowed").GetBoolean());Assert.False(x.GetProperty("AutomaticActivation").GetBoolean());});
        Assert.Equal(before.Count,Directory.GetFiles(reports.Root,"*",SearchOption.AllDirectories).Length);
        foreach(var pair in before)Assert.Equal(pair.Value,File.ReadAllBytes(pair.Key));
        Assert.DoesNotContain("https://",output.ToString());
    }
    [Fact]
    public async Task Legacy_flat_SOL_files_do_not_fallback_when_requested_subdirectory_is_missing()
    {
        if(!OperatingSystem.IsLinux())return;
        using var reports=new Reports();
        foreach(var file in Directory.GetFiles(Path.Combine(reports.Root,"SOL-EUR")))File.Move(file,Path.Combine(reports.Root,Path.GetFileName(file)));
        Directory.Delete(Path.Combine(reports.Root,"SOL-EUR"));using var output=new StringWriter();
        await LinuxDemoHost.RunAdvisoryForTestsAsync(reports.Root,new StringReader("ADVISORY-STATUS\nQUIT\n"),output,OkxAdvisoryTests.Now,CancellationToken.None);
        Assert.Contains("REPORT_FILES_UNAVAILABLE",output.ToString());Assert.DoesNotContain("AGREE_SELL",output.ToString());
    }
    private sealed class RefusalWriter:StringWriter
    {
        internal readonly TaskCompletionSource<bool> Seen=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void WriteLine(string? value){base.WriteLine(value);if(value=="COMMAND_REFUSED")Seen.TrySetResult(true);}
    }
    [Fact]
    public async Task Main_host_dispatch_denies_nonliteral_advisory_tokens_before_reader()
    {
        if(!OperatingSystem.IsLinux())return;
        // Generic host friend has no real report-root or credential fallback.
        string[] bad={"ADVISORY-STATUS btc-eur","ADVISORY-STATUS BTC-USDT","ADVISORY-STATUS XRP-EUR","ADVISORY-STATUS ../SOL-EUR",
            "ADVISORY-STATUS SOL-EUR/../../BTC-EUR","ADVISORY-STATUS  BTC-EUR","ADVISORY-STATUS BTC-EUR ","ADVISORY-STATUS BTC-EUR ETH-EUR"};
        foreach(string command in bad)
        {
            using var output=new RefusalWriter();var quit=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);int reads=0;var type=typeof(LinuxDemoHost);
            var host=type.GetConstructors(BindingFlags.NonPublic|BindingFlags.Instance).Single().Invoke(new object?[]{"/not-opened-by-advisory",null,
                (Func<CancellationToken,ValueTask<string?>>)(ct=>new ValueTask<string?>(Interlocked.Increment(ref reads)==1?Task.FromResult<string?>(command):quit.Task.WaitAsync(ct))),output,null,null,(Func<bool>)(()=>true),CancellationToken.None});
            var run=(Task<int>)type.GetMethod("RunCoreAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(host,null)!;
            try{await output.Seen.Task.WaitAsync(TimeSpan.FromSeconds(5));}
            finally{quit.TrySetResult("QUIT");}
            int code=await run;
            Assert.Equal(0,code);Assert.Equal(1,output.ToString().Split('\n').Count(x=>x.TrimEnd('\r')=="COMMAND_REFUSED"));
            Assert.DoesNotContain("ADVISORY_STATUS ",output.ToString());
        }
    }
}
