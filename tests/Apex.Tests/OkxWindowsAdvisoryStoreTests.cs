using System;
using System.IO;
using System.Reflection;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Tests;
public sealed class OkxWindowsAdvisoryStoreTests
{
    [Fact][Trait("Gate","WindowsAdvisoryIntegration")]
    public void Native_consumer_pins_pointer_through_send_and_refuses_replacement_then_reopens()
    {
        Assert.True(OperatingSystem.IsWindows());
        string root=Path.Combine(Path.GetTempPath(),"apex-advisory-consumer-"+Guid.NewGuid().ToString("N"));
        using var guard=WindowsPrivateData.Open(root,AppContext.BaseDirectory);
        var fixture=new OkxWindowsAdvisoryConsumerTests.Reports();fixture.Build();
        foreach(var row in fixture.Files){string p=Path.Combine(root,"BTC-EUR",row.Key);Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllBytes(p,row.Value);}
        var type=typeof(OkxAdvisoryComparison).Assembly.GetType("Apex.Desktop.Okx.WindowsOkxAdvisoryStore");Assert.NotNull(type);
        using var store=(IDisposable)Activator.CreateInstance(type!,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{root},null)!;
        var method=type!.GetMethod("Acquire",BindingFlags.Instance|BindingFlags.NonPublic)!;
        object[] args={"BTC-EUR",fixture.Digest,OkxWindowsAdvisoryConsumerTests.Cutoff,OkxWindowsAdvisoryConsumerTests.Cutoff.AddHours(12)};
        using(var lease=(IDisposable?)method.Invoke(store,args))
        {
            Assert.NotNull(lease);string current=Path.Combine(root,"BTC-EUR","CURRENT.json"),temp=current+".new";File.WriteAllBytes(temp,fixture.Files["CURRENT.json"]);
            Assert.ThrowsAny<IOException>(()=>File.Move(temp,current,true));
        }
        File.Move(Path.Combine(root,"BTC-EUR","CURRENT.json.new"),Path.Combine(root,"BTC-EUR","CURRENT.json"),true);
        using var admitted=(IDisposable?)method.Invoke(store,args);Assert.NotNull(admitted);
    }
    [Theory][InlineData("acl")][InlineData("link")][InlineData("junction")]
    [Trait("Gate","WindowsAdvisoryIntegration")]
    public void Unsafe_existing_descendants_refuse_without_repair(string mode)
    {
        Assert.True(OperatingSystem.IsWindows());
        string root=Path.Combine(Path.GetTempPath(),"apex-advisory-unsafe-"+Guid.NewGuid().ToString("N"));
        using var guard=WindowsPrivateData.Open(root,AppContext.BaseDirectory);string file=Path.Combine(root,"unrelated.json");File.WriteAllText(file,"{}");
        var process=new System.Diagnostics.ProcessStartInfo{FileName=mode=="acl"?"icacls":"cmd",UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string a in mode=="acl"?new[]{file,"/grant","*S-1-1-0:F"}:mode=="link"?new[]{"/c","mklink","/H",Path.Combine(root,"alias.json"),file}:new[]{"/c","mklink","/J",Path.Combine(root,"BTC-EUR"),Path.GetDirectoryName(root)!})process.ArgumentList.Add(a);
        using(var p=System.Diagnostics.Process.Start(process)!){p.WaitForExit();Assert.Equal(0,p.ExitCode);}
        var type=typeof(OkxAdvisoryComparison).Assembly.GetType("Apex.Desktop.Okx.WindowsOkxAdvisoryStore");Assert.NotNull(type);
        Assert.Throws<TargetInvocationException>(()=>Activator.CreateInstance(type!,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{root},null));Assert.Equal("{}",File.ReadAllText(file));
    }
}
