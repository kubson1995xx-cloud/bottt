using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;

public sealed class OkxPendingAlgoDiagnosticTests
{
    [Fact]
    public async Task Real_host_pending_diagnostic_reaches_three_filters_without_economic_writes_and_resets()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));await f.Service.DisconnectAsync();
        await using var runtime=OkxNavalRuntime.ComposeLinuxDemo(f.Root,f.Binding,f,f,()=>true);
        await using var account=OkxAccountConnectionService.ForTests(runtime,f.Root,f.Binding,f,f.Clock,
            (s,k,d,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,d,x,(t,c)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
            (s,k,d,x)=>OkxObservationStream.ForTests((u,c)=>Task.FromResult<WebSocket>(new OperationalSocket(u)),f.Clock,s,k,d,x),()=>OkxNavalStore.ForTests(f.Root,f.Binding));
        var request=f.Request with{Session=Guid.NewGuid()};var lease=await runtime.Activation.RequestAsync(request,default);Assert.True(await account.ConnectAsync(request,lease!,default),account.Status);
        using var peer=new TradingPeer(f);await using var loop=OkxOperationalTradingService.Create(account,f.Clock,(s,k,d,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,d,x),peer);
        var inputs=Channel.CreateUnbounded<string>();using var output=new Lines();var type=typeof(LinuxDemoHost);
        var host=type.GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single().Invoke(new object?[]{f.Root,null,(Func<CancellationToken,ValueTask<string?>>)(async ct=>await inputs.Reader.ReadAsync(ct)),output,null,null,(Func<bool>)(()=>true),CancellationToken.None});
        var run=(Task<int>)type.GetMethod("RunCoreAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(host,null)!;
        await output.Until("LOCAL DEMO:");
        try
        {
            inputs.Writer.TryWrite("DIAGNOSE-PENDING");await output.Until("PENDING_DIAGNOSE_REFUSED_STOP_UNARMED_ORDINARY_REQUIRED");
            type.GetField("loop",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(host,loop);type.GetField("account",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(host,account);
            f.Mutate=(path,body)=>
            {
                Assert.Equal("/api/v5/trade/orders-algo-pending",path);
                body["msg"]="PRIVATE_PROVIDER_TEXT";
                string query=f.Paths.Last();
                if(query.Contains("ordType=conditional",StringComparison.Ordinal)){body["code"]="51000";body["data"]=new JsonArray();}
                else if(query.Contains("ordType=trigger",StringComparison.Ordinal))body["data"]=123;
                else body["data"]=new JsonArray(new JsonObject{["instType"]="SPOT",["tdMode"]="cash",["PRIVATE_PROVIDER_FIELD"]="PRIVATE_PROVIDER_VALUE"});
            };
            int start=f.Paths.Count;byte[] before=File.ReadAllBytes(f.Target);
            inputs.Writer.TryWrite("DIAGNOSE-PENDING");await output.Until("PENDING_DIAGNOSE_INCOMPLETE failures=3");
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(f.Target)),"diagnostic must leave exact durable target unchanged before teardown");
            Assert.Equal(3,f.Paths.Count-start);Assert.All(f.Paths.Skip(start),p=>Assert.StartsWith("/api/v5/trade/orders-algo-pending?",p));Assert.Equal(0,peer.Posts);
            var rows=output.All.Where(l=>l.StartsWith("PENDING_ALGO_FAILURE ",StringComparison.Ordinal)).Select(l=>JsonNode.Parse(l.Substring(21))!).ToArray();
            Assert.Equal(3,rows.Length);Assert.Equal(new[]{"ConditionalAndOco","Trigger","TrailingStop"},rows.Select(r=>r["Filter"]!.GetValue<string>()));
            Assert.Equal("51000",rows[0]["ErrorCode"]!.GetValue<string>());Assert.Equal("VENUE_REFUSAL",rows[0]["Diagnostic"]!["Phase"]!.GetValue<string>());Assert.Equal("51000",rows[0]["Diagnostic"]!["OkxCode"]!.GetValue<string>());
            Assert.All(rows,r=>Assert.Equal(200,r["Diagnostic"]!["HttpStatus"]!.GetValue<int>()));
            Assert.Equal("InvalidPayload",rows[1]["ErrorCode"]!.GetValue<string>());Assert.Equal("0",rows[1]["Diagnostic"]!["OkxCode"]!.GetValue<string>());Assert.Equal("$.data",rows[1]["Diagnostic"]!["Payload"]!["Path"]!.GetValue<string>());Assert.Equal("Number",rows[1]["Diagnostic"]!["Payload"]!["Actual"]!.GetValue<string>());
            Assert.Equal("$.data[].<unknown>",rows[2]["Diagnostic"]!["Payload"]!["Path"]!.GetValue<string>());
            string text=string.Join("\n",rows.Select(r=>r.ToJsonString()));Assert.DoesNotContain("PRIVATE_",text);Assert.DoesNotContain("Digest",text);Assert.DoesNotContain("Query",text);Assert.DoesNotContain(f.Binding.Account.AccountId,text);Assert.DoesNotContain(System.Text.Encoding.ASCII.GetString(OperationalPeer.Key),text);
            f.Mutate=(p,b)=>{Assert.Equal("/api/v5/trade/orders-algo-pending",p);};
            start=f.Paths.Count;inputs.Writer.TryWrite("DIAGNOSE-PENDING");await output.Until("PENDING_DIAGNOSE_COMPLETE failures=0");Assert.Equal(3,f.Paths.Count-start);Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(f.Target)));Assert.Equal(3,output.All.Count(l=>l.StartsWith("PENDING_ALGO_FAILURE ",StringComparison.Ordinal)));
            request=request with{Purpose=OkxActivationPurpose.Execute,Session=account.TradingSessionId};lease=await runtime.Activation.RequestAsync(request,default);Assert.True(loop.Arm(request,lease!));before=File.ReadAllBytes(f.Target);start=f.Paths.Count;
            inputs.Writer.TryWrite("DIAGNOSE-PENDING");await output.Until("PENDING_DIAGNOSE_REFUSED_STOP_UNARMED_ORDINARY_REQUIRED");Assert.Equal(start,f.Paths.Count);Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(f.Target)));Assert.Equal(0,peer.Posts);
        }
        finally{f.Mutate=null;inputs.Writer.TryWrite("QUIT");await run.WaitAsync(TimeSpan.FromSeconds(10));}
        Assert.Equal(0,await run);
    }
    private sealed class Lines:TextWriter
    {
        private readonly Channel<string> lines=Channel.CreateUnbounded<string>();internal readonly List<string> All=new();
        public override System.Text.Encoding Encoding=>System.Text.Encoding.UTF8;
        public override void WriteLine(string? value){lock(All){All.Add(value??"");}lines.Writer.TryWrite(value??"");}
        internal async Task Until(string prefix)
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try{while(!(await lines.Reader.ReadAsync(timeout.Token)).StartsWith(prefix,StringComparison.Ordinal)){} }
            catch(OperationCanceledException){throw new Xunit.Sdk.XunitException("Missing bounded host result: "+prefix+"; output="+string.Join("|",All));}
        }
    }
}
