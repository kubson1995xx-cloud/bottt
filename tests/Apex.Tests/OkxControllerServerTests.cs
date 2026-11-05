using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;

public sealed partial class OkxControllerServerTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper log;
    public OkxControllerServerTests(Xunit.Abstractions.ITestOutputHelper log){this.log=log;}
    [Fact]
    public async Task Authenticated_real_relay_projects_disconnected_existing_host_without_private_access()
    {
        string root=Path.Combine("/home/ubuntu/apex-deliveries","cs-"+Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        string financial=Path.Combine(root,"never-open-financial");int discoveries=0;
        using var stop=new CancellationTokenSource();
        await using var server=LinuxControllerServer.ForTests(root,financial,new LinuxDemoCredentialVault(Path.Combine(root,"never-open-vault")),(_,_)=>{Interlocked.Increment(ref discoveries);throw new Exception("PRIVATE_FORBIDDEN");});
        var running=server.RunAsync(stop.Token);
        try
        {
            using var relay=await LinuxControllerRelay.ConnectForTestsAsync(root,default);
            var hello=await relay.HelloAsync(default);
            var request=new OkxControllerCommand(OkxControllerProtocol.Version,hello.Epoch,hello.Session,Guid.NewGuid().ToString("N"),"SNAPSHOT");
            var receipt=await relay.CommandAsync(request,default);
            Assert.Equal("confirmed",receipt.Outcome);Assert.NotNull(receipt.Snapshot);Assert.False(receipt.Snapshot!.Connected);Assert.Equal("STOP",receipt.Mode);Assert.False(receipt.Armed);
            Assert.Equal(request.CommandId,receipt.CommandId);Assert.Equal(hello.Session,receipt.Session);
            Assert.Equal(OkxControllerProtocol.EncodeReceipt(receipt),OkxControllerProtocol.EncodeReceipt(await relay.CommandAsync(request,default)));
            Assert.Equal("refused",(await relay.CommandAsync(request with{Action="CONNECT"},default)).Outcome);
            Assert.False(Directory.Exists(financial));Assert.Equal(0,discoveries);
            foreach(var a in new[]{typeof(LinuxDemoHost).Assembly,typeof(OkxControllerProtocol).Assembly,typeof(OkxControllerServerTests).Assembly})log.WriteLine("CONSUMED "+a.GetName().Name+" "+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(a.Location)))+" "+a.Location);
        }
        finally{stop.Cancel();await running.WaitAsync(TimeSpan.FromSeconds(10));}
        Assert.False(File.Exists(Path.Combine(root,"controller.sock")));Directory.Delete(root,true);
    }
}
