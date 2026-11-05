using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;
public sealed partial class OkxControllerServerTests
{
    [System.Runtime.InteropServices.DllImport("libc")]private static extern uint umask(uint mode);
    private sealed class FixtureMask:IDisposable{private readonly uint previous=umask(0x3f);public void Dispose()=>umask(previous);}
    [Fact]
    public async Task Explicit_CONNECT_reopens_only_same_owner_using_isolated_encrypted_vault_then_ARM_START_STOP()
    {
        using var mask=new FixtureMask();
        string root=FreshRoot(),financial=Path.Combine(root,"financial");
        var binding=OkxConfiguration.Bind(new OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId="linux-demo-bot",Environment="OkxDemo",Region="EEA",ExpectedUid="9440000001",ExpectedMainUid="9440000001",QuoteCurrency="USDC",CredentialBindingId="native-demo-vault",SafetyVersion="okx-cash-v1"}}})[0];
        await using var f=new OperationalPeer(financial,binding);await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));await f.Service.DisconnectAsync();
        foreach(string p in Directory.GetDirectories(financial,"*",SearchOption.AllDirectories))File.SetUnixFileMode(p,(UnixFileMode)448);File.SetUnixFileMode(financial,(UnixFileMode)448);
        foreach(string p in Directory.GetFiles(financial,"*",SearchOption.AllDirectories))File.SetUnixFileMode(p,(UnixFileMode)384);
        using var cryptor=new FixtureCryptor();var vault=new LinuxDemoCredentialVault(Path.Combine(root,"vault"),null,cryptor);await vault.SaveNewAsync(OperationalPeer.Key,OperationalPeer.Secret,OperationalPeer.Pass);
        int discoveries=0;using var peer=new TradingPeer(f){ExitSignal=true};
        var transport=new FiniteEntryTestComposition(f.Clock,(s,k,h,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x,(t,c)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
            (s,k,h,x)=>OkxObservationStream.ForTests((u,c)=>Task.FromResult<WebSocket>(new OperationalSocket(u)),f.Clock,s,k,h,x),(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);
        await using var server=LinuxControllerServer.ForTests(root,financial,vault,(k,c)=>{discoveries++;return Task.FromResult(new OkxEnrollmentDiscovery(new(binding.Account.AccountId,binding.MainUid,"Demo","DemoTradingApi",k.Fingerprint,DateTimeOffset.UtcNow,ImmutableArray<OkxExecutionBalanceDetailDto>.Empty,ImmutableArray<OkxExecutionInstrumentDto>.Empty),"SYNTHETIC"));},transport);
        using var stop=new CancellationTokenSource();var running=server.RunAsync(stop.Token);
        try
        {
            using var relay=await LinuxControllerRelay.ConnectForTestsAsync(root,default);var h=await relay.HelloAsync(default);Assert.Equal(0,discoveries);
            foreach(string action in new[]{"CONNECT","ARM","START","STOP"})
            {
                var r=await relay.CommandAsync(Command(h,action),default);
                if(r.Outcome!="confirmed")
                {
                    const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                    var attachment=typeof(LinuxControllerServer).GetField("host",flags)!.GetValue(server)!;var owner=attachment.GetType().GetField("host",flags)!.GetValue(attachment)!;
                    var loop=(OkxOperationalTradingService)typeof(LinuxDemoHost).GetField("loop",flags)!.GetValue(owner)!;
                    Assert.True(false,action+":"+r.Code+" status="+System.Text.Json.JsonSerializer.Serialize(loop.ReadStatus())+" start="+loop.Start().Code);
                }
            }
            Assert.Equal(1,discoveries);Assert.Equal(0,peer.Posts);
            Assert.Equal("confirmed",(await relay.CommandAsync(Command(h,"DISCONNECT"),default)).Outcome);
        }
        finally{stop.Cancel();await running.WaitAsync(TimeSpan.FromSeconds(10));}
        // Financial fixture owns its own subtree and removes it on disposal.
        File.Delete(Path.Combine(root,".controller.lock"));Directory.Delete(Path.Combine(root,"vault"),true);
    }
    // Synthetic-only in-process AES-GCM. Never replaces the production native cryptor.
    private sealed class FixtureCryptor:ICredentialCryptor,IDisposable
    {
        private readonly byte[] key=RandomNumberGenerator.GetBytes(32);
        public Task<SensitiveBuffer> TransformAsync(bool encrypt,ReadOnlyMemory<byte> input,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();int n=encrypt?input.Length+28:input.Length-28;var b=new SensitiveBuffer(HeapBufferAllocator.Instance,n){Length=n};
            using var aes=new AesGcm(key,16);
            if(encrypt){RandomNumberGenerator.Fill(b.Array.AsSpan(0,12));aes.Encrypt(b.Array.AsSpan(0,12),input.Span,b.Array.AsSpan(28),b.Array.AsSpan(12,16));}
            else aes.Decrypt(input.Span[..12],input.Span[28..],input.Span.Slice(12,16),b.Array);
            return Task.FromResult(b);
        }
        public void Dispose()=>CryptographicOperations.ZeroMemory(key);
    }
}
