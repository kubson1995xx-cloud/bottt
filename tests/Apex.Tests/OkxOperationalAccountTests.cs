using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;

namespace Apex.Tests;

public sealed class OkxOperationalAccountTests
{
    [Theory]
    [InlineData("AfterTempFlush",false)]
    [InlineData("AfterReplace",true)]
    public async Task Killed_baseline_writer_reopens_only_actual_target_and_preserves_orphan(string boundary,bool committed)
    {
        await using var f=new OperationalPeer();string barrier=Path.Combine(f.Root,"crash-barrier");
        string binding=barrier+".binding.json";File.WriteAllText(binding,System.Text.Json.JsonSerializer.Serialize(f.Binding));
        var start=new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{typeof(SyntheticHostEntry).Assembly.Location,"--okx-baseline-crash",f.Root,binding,barrier,boundary})start.ArgumentList.Add(arg);
        using var child=System.Diagnostics.Process.Start(start)!;
        try
        {
            var wait=System.Diagnostics.Stopwatch.StartNew();while(!File.Exists(barrier)&&!child.HasExited&&wait.Elapsed<TimeSpan.FromSeconds(20))await Task.Delay(25);
            Assert.True(File.Exists(barrier),"Child did not reach exact baseline barrier");child.Kill(true);await child.WaitForExitAsync();
            Assert.Equal(committed,File.Exists(f.Target));
            var orphans=Directory.EnumerateFiles(Path.GetDirectoryName(f.Target)!,"*.tmp").ToArray();Assert.Equal(committed?0:1,orphans.Length);
            Assert.Equal(committed,f.Service.OpenExisting());Assert.All(orphans,p=>Assert.True(File.Exists(p)));
            if(committed)Assert.Equal(1234.56m,f.Service.Coordinator!.Snapshot().Cash);
        }
        finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}
    }
    internal static async Task<int> CrashBaseline(string[] args)
    {
        if(args.Length!=5)return 98;
        var binding=System.Text.Json.JsonSerializer.Deserialize<OkxScopeBinding>(File.ReadAllText(args[2]))!;
        await using var f=new OperationalPeer(args[1],binding,preserve:true);await f.Connect();
        f.WriteBoundary=b=>{if(b.ToString()!=args[4])return;File.WriteAllText(args[3],"READY "+b);using var hold=new ManualResetEventSlim(false);hold.Wait(TimeSpan.FromSeconds(25));throw new IOException("Parent did not kill baseline writer");};
        return await f.Service.ApproveAndCreateAsync(default)?0:99;
    }
    [Fact]
    public async Task Fresh_process_reopens_exact_owner_without_secrets_or_approval()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await f.Service.DisconnectAsync();string bytes=File.ReadAllText(f.Target);int opens=f.SecretOpens;
        await using var restarted=OkxNavalRuntime.Compose(f.Root,new[]{f.Binding},f,f);
        var service=OkxAccountConnectionService.ForTests(restarted,f.Root,f.Binding,f,f.Clock,
            (s,p,h,x)=>throw new Exception("Restart opened transport"),(s,p,h,x)=>throw new Exception("Restart opened stream"));
        Assert.True(service.OpenExisting());Assert.Equal(1234.56m,service.Coordinator!.Snapshot().Cash);
        Assert.Equal("STOP",service.Coordinator.Snapshot().Mode);Assert.Equal(opens,f.SecretOpens);Assert.Equal(1,f.Approvals);Assert.Equal(bytes,File.ReadAllText(f.Target));
    }
    [Fact]
    public async Task Separate_exact_approval_rechecks_identity_before_financial_creation()
    {
        await using var f=new OperationalPeer();await f.Connect();
        f.OnApproval=()=>f.Mutate=(path,body)=>{if(path=="/api/v5/account/config")body["data"]![0]!["uid"]="999999";};
        Assert.False(await f.Service.ApproveAndCreateAsync(default));Assert.False(File.Exists(f.Target));
    }
    [Theory]
    [InlineData("before")]
    [InlineData("after-replace")]
    [InlineData("monotonic")]
    public async Task Original_candidate_expiry_is_not_renewed_by_approval_or_persistence(string phase)
    {
        await using var f=new OperationalPeer();await f.Connect();
        if(phase=="after-replace")f.WriteBoundary=b=>{if(b==AtomicWriteBoundary.AfterReplace)f.Clock.Advance(TimeSpan.FromSeconds(31));};
        else f.OnApproval=()=>f.Clock.Advance(TimeSpan.FromSeconds(31),phase!="monotonic",true);
        Assert.False(await f.Service.ApproveAndCreateAsync(default));Assert.Null(f.Service.Coordinator);
        Assert.Equal(phase=="after-replace",File.Exists(f.Target));
        await f.Service.DisconnectAsync();
        if(phase=="after-replace"){Assert.True(f.Service.OpenExisting());Assert.Equal(1234.56m,f.Service.Coordinator!.Snapshot().Cash);}
    }
    [Fact]
    public async Task Offline_receipt_permit_and_seed_cannot_mutate_broker_owner_and_approval_cannot_replay()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        var c=f.Service.Coordinator!;string bytes=File.ReadAllText(f.Target);var view=c.Snapshot();
        var receipt=new NavalEvidenceReceipt(c.OwnerToken,new("okx-"+f.Binding.ScopeId,"USDC"),"forged",1,1,f.Clock.GetUtcNow(),f.Clock.GetUtcNow(),NavalEvidenceKind.Reconcile,true,true,new Dictionary<string,decimal>{{"BTC-USDC",1m}},cash:new[]{new NavalCashAccount(f.Binding.Account,999999m,999999m)});
        Assert.Equal(NavalMutationStatus.Refused,c.Reconcile(receipt).Status);
        Assert.Equal(NavalMutationStatus.Refused,c.Start(new NavalStartPermit(c.OwnerToken,view.DurableRevision,0,true)).Status);
        Assert.False(await f.Service.ApproveAndCreateAsync(default));Assert.Equal(1,f.Approvals);Assert.Equal(bytes,File.ReadAllText(f.Target));
    }
    [Fact]
    public async Task Duplicate_owner_missing_target_or_manifest_never_reseed()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        var e=System.Text.Json.JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(File.ReadAllText(f.Target))!;
        using(var duplicate=OkxNavalStore.ForTests(f.Root,f.Binding))Assert.ThrowsAny<IOException>(()=>NavalBrokerSession.OpenExisting(duplicate,e.NavalBroker!.Baseline.Binding,new BrokerClock(f.Clock)));
        await f.Service.DisconnectAsync();File.Delete(f.Target);Assert.False(f.Service.OpenExisting());Assert.False(File.Exists(f.Target));
    }
    [Theory]
    [InlineData("uid")]
    [InlineData("liability")]
    [InlineData("inventory")]
    [InlineData("fee")]
    public async Task Original_observations_must_establish_clean_exact_account(string mutation)
    {
        await using var f=new OperationalPeer();
        f.Mutate=(path,body)=>
        {
            if(mutation=="uid"&&path=="/api/v5/account/config")body["data"]![0]!["uid"]="999999";
            if(mutation=="liability"&&path=="/api/v5/account/balance")body["data"]![0]!["details"]![0]!["liab"]="";
            if(mutation=="inventory"&&path=="/api/v5/account/balance")body["data"]![0]!["details"]![0]!["ccy"]="BTC";
            if(mutation=="fee"&&path=="/api/v5/account/trade-fee")body["data"]![0]!["feeGroup"]![0]!["groupId"]="777";
        };
        var lease=await f.Runtime.Activation.RequestAsync(f.Request,default);Assert.False(await f.Service.ConnectAsync(f.Request,lease!,default));Assert.False(File.Exists(f.Target));Assert.Equal(0,f.Approvals);
        Assert.All(f.Sockets,s=>Assert.Equal(WebSocketState.Closed,s.State));
    }
    private sealed class BrokerClock(TimeProvider clock):INavalBrokerClock
    {public DateTimeOffset UtcNow=>clock.GetUtcNow();public long Timestamp=>clock.GetTimestamp();public long TimestampFrequency=>clock.TimestampFrequency;}
    [Fact]
    public async Task No_serializable_baseline_factory_can_bypass_registered_candidate_and_approval()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        var baseline=System.Text.Json.JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(File.ReadAllText(f.Target))!.NavalBroker!.Baseline;
        Assert.DoesNotContain(typeof(NavalCoreStateOwner).GetMethods(System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic),m=>m.Name=="CreateBroker"&&m.GetParameters().Any(p=>p.ParameterType==typeof(NavalBrokerBaseline)));
        using var store=OkxNavalStore.ForTests(f.Root,f.Binding);
        var bootstrap=new NavalBrokerBootstrap(baseline.Binding,new BrokerClock(f.Clock),f.Service);
        Assert.ThrowsAny<Exception>(()=>bootstrap.InitializeBaseline(new NavalBaselineCandidate(),new NavalBaselineApproval(),store));
    }
    [Theory]
    [InlineData("BeforeTempCreate",false)]
    [InlineData("BeforeReplace",false)]
    [InlineData("AfterReplace",true)]
    public async Task Actual_atomic_baseline_write_failure_preserves_target_and_never_retries_seed(string boundary,bool committed)
    {
        await using var f=new OperationalPeer();await f.Connect();
        f.WriteBoundary=b=>{if(b.ToString()==boundary)throw new IOException("synthetic write failure");};
        Assert.False(await f.Service.ApproveAndCreateAsync(default));Assert.Null(f.Service.Coordinator);
        Assert.Equal(committed,File.Exists(f.Target));Assert.False(await f.Service.ApproveAndCreateAsync(default));Assert.Equal(1,f.Approvals);
        f.WriteBoundary=null;await f.Service.DisconnectAsync();Assert.Equal(committed,f.Service.OpenExisting());
        if(committed)Assert.Equal(1234.56m,f.Service.Coordinator!.Snapshot().Cash);
    }
    [Theory]
    [InlineData("root")]
    [InlineData("environment")]
    [InlineData("config")]
    [InlineData("missing-marker")]
    [InlineData("schema")]
    public async Task Reopen_binding_and_manifest_mismatch_preserves_original(string change)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));await f.Service.DisconnectAsync();
        string bytes=File.ReadAllText(f.Target);var saved=System.Text.Json.JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(bytes)!;
        var binding=saved.NavalBroker!.Baseline.Binding;
        if(change=="missing-marker")File.Delete(Path.Combine(Path.GetDirectoryName(f.Target)!,NavalBrokerValidation.Marker));
        if(change=="schema"){var n=JsonNode.Parse(bytes)!;n["NavalBroker"]!.AsObject().Remove("Schema");File.WriteAllText(f.Target,n.ToJsonString());}
        if(change=="environment")binding=binding with{Account=binding.Account with{Environment=NavalEnvironment.Live}};
        if(change=="config")binding=binding with{ConfigDigest=new string('A',64)};
        string root=change=="root"?f.Root+"-copy":f.Root;
        try
        {
            using var store=OkxNavalStore.ForTests(root,f.Binding);
            if(change=="root")foreach(string p in Directory.EnumerateFiles(Path.GetDirectoryName(f.Target)!))File.Copy(p,Path.Combine(store.DirectoryPath,Path.GetFileName(p)));
            string before=File.ReadAllText(Path.Combine(store.DirectoryPath,OkxNavalStore.Target));
            Assert.ThrowsAny<Exception>(()=>NavalBrokerSession.OpenExisting(store,binding,new BrokerClock(f.Clock)));
            Assert.Equal(before,File.ReadAllText(Path.Combine(store.DirectoryPath,OkxNavalStore.Target)));
        }
        finally{if(root!=f.Root)Directory.Delete(root,true);}
    }
    [Fact]
    public async Task Authorized_observation_exact_approval_real_FileStore_disconnect_and_reopen_STOP()
    {
        await using var f = new OperationalPeer();
        await f.Runtime.StartAsync(default);
        Assert.Equal(0, f.SecretOpens); Assert.Empty(f.Paths);
        var lease = await f.Runtime.Activation.RequestAsync(f.Request, default);
        Assert.NotNull(lease);
        Assert.True(await f.Service.ConnectAsync(f.Request, lease!, default));
        Assert.Equal("BASELINE_APPROVAL_REQUIRED", f.Service.Status);
        Assert.False(File.Exists(f.Target));
        Assert.Equal(2, f.Sockets.Count);
        Assert.Contains("/api/v5/account/config", f.Paths);
        Assert.Contains(f.Paths, p => p.StartsWith("/api/v5/trade/fills?", StringComparison.Ordinal));
        Assert.True(await f.Service.ApproveAndCreateAsync(default));
        Assert.Equal(1, f.Approvals);
        Assert.Equal(1234.56m, f.Service.Coordinator!.Snapshot().Cash);
        Assert.Equal("STOP", f.Service.Coordinator.Snapshot().Mode);
        Assert.Null(f.Service.Coordinator.ReadPublication());
        string bytes = File.ReadAllText(f.Target);
        Assert.Contains("naval-broker-v2", bytes);
        Assert.DoesNotContain("naval-execution-offline-v1", bytes);
        await f.Service.DisconnectAsync();
        bytes=File.ReadAllText(f.Target);
        Assert.Null(f.Service.Coordinator);
        Assert.True(f.Service.OpenExisting());
        Assert.Equal(1234.56m, f.Service.Coordinator!.Snapshot().Cash);
        Assert.Equal("STOP", f.Service.Coordinator.Snapshot().Mode);
        Assert.Equal(bytes, File.ReadAllText(f.Target));
        Assert.Equal(1, f.Approvals);
        Assert.All(f.Methods, m => Assert.Equal(HttpMethod.Get, m));
    }
}

internal sealed class OperationalPeer : HttpMessageHandler, IOkxExecutionSecretProvider, IOkxProtectedActivationProvider, IOkxProtectedBaselineProvider, IAsyncDisposable
{
    internal readonly string Root;
    private readonly bool native,preserve;
    internal readonly OperationalClock Clock = new();
    internal readonly string BalanceUtc;
    internal readonly OkxScopeBinding Binding;
    internal readonly OkxNavalRuntime Runtime;
    internal readonly OkxAccountConnectionService Service;
    internal readonly OkxActivationRequest Request;
    internal readonly List<string> Paths = new();
    internal readonly List<HttpMethod> Methods = new();
    internal readonly List<OperationalSocket> Sockets = new();
    internal int SecretOpens, Approvals;
    internal Action? OnApproval;
    internal Func<OkxActivationRequest,CancellationToken,ValueTask<bool>>? ActivationOverride;
    internal Action<string,JsonNode>? Mutate;
    internal Action<AtomicWriteBoundary>? WriteBoundary;
    internal IOkxProtectedBaselineProvider? BaselineOverride;
    internal async Task Connect(){if(Runtime.IsFiniteDemo){Assert.True(await Service.ConnectFiniteDemoAsync(Request.CredentialFingerprint,default),Service.Status);return;}var lease=await Runtime.Activation.RequestAsync(Request,default);Assert.NotNull(lease);Assert.True(await Service.ConnectAsync(Request,lease!,default),Service.Status);}
    internal string Target => Path.Combine(OkxConfiguration.Paths(Root, Binding).Authority, "naval_authority_broker_v2.json");
    internal static readonly byte[] Key=Encoding.ASCII.GetBytes("operational-synthetic-key"), Secret=Encoding.ASCII.GetBytes("operational-synthetic-secret"), Pass=Encoding.ASCII.GetBytes("operational-synthetic-pass");
    internal OperationalPeer(string? root=null,OkxScopeBinding? binding=null,bool native=false,bool preserve=false,bool finite=false,bool seedCredential=true)
    {
        this.native=native;this.preserve=preserve;
        ResetStreamPacing();
        Root=root??Path.Combine(native?Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData):(Environment.GetEnvironmentVariable("TMPDIR")??Path.GetTempPath()),"okx-operation-"+Guid.NewGuid().ToString("N"));
        if(native&&OperatingSystem.IsWindows()){using var admission=WindowsPrivateData.Open(Root,AppContext.BaseDirectory);}
        else Directory.CreateDirectory(Root);
        BalanceUtc=Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString();
        string uid=Interlocked.Increment(ref nextUid).ToString();
        Binding=binding??OkxConfiguration.Bind(new OkxNavalOptions { Schema="okx-naval-config-v1", Scopes=new[] { new OkxScopeOptions { ScopeId="account",Environment="OkxDemo",Region="EEA",ExpectedUid=uid,ExpectedMainUid=uid,QuoteCurrency="USDC",CredentialBindingId="key",SafetyVersion="okx-cash-v1" } } })[0];
        if(native&&seedCredential)new WindowsOkxCredentialStore(Root).Create(Binding,OkxConfiguration.Paths(Root,Binding),"DemoTradingApi",Key,Secret,Pass);
        Runtime=finite?OkxNavalRuntime.ComposeFiniteDemo(Root,Binding,this,()=>true):OkxNavalRuntime.Compose(Root,new[]{Binding},this,this);
        Request=new(Binding,Convert.ToHexString(SHA256.HashData(Key)).ToLowerInvariant(),Guid.NewGuid(),0,OkxActivationPurpose.Observe,DateTimeOffset.UtcNow.AddSeconds(120));
        Service=OkxAccountConnectionService.ForTests(Runtime,Root,Binding,this,Clock,
            (scope,provider,fp,fence)=>OkxExecutionObservationClient.ForTests(this,Clock,scope,provider,fp,fence,(t,ct)=>{Clock.Advance(t);return Task.CompletedTask;}),
            (scope,provider,fp,fence)=>OkxObservationStream.ForTests((uri,ct)=> { var s=new OperationalSocket(uri);Sockets.Add(s);return Task.FromResult<WebSocket>(s); },Clock,scope,provider,fp,fence),()=>native?NativeObservedStore():OkxNavalStore.ForTests(Root,Binding,b=>WriteBoundary?.Invoke(b)));
    }
    private OkxNavalStore NativeObservedStore()=>(OkxNavalStore)Activator.CreateInstance(typeof(OkxNavalStore),System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic,null,new object[]{Root,Binding,false,(Action<AtomicWriteBoundary>)(b=>WriteBoundary?.Invoke(b)),false},null)!;
    private static int nextUid=910000;
    // Each synthetic peer owns an independent synthetic clock. The production stream keeps a
    // process-wide connect-pacing deadline (one real clock); a previous peer's ADVANCED synthetic
    // clock must not leave that deadline minutes ahead of this peer's clock. Test isolation only.
    internal static void ResetStreamPacing()
    {
        var t=typeof(OkxObservationStream);const System.Reflection.BindingFlags F=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
        t.GetField("nextConnect",F)!.SetValue(null,default(DateTimeOffset));t.GetField("nextClock",F)!.SetValue(null,null);t.GetField("nextMono",F)!.SetValue(null,0L);
    }
    public OkxExecutionCredentialLease Open(OkxScopeBinding binding,OkxScopePaths paths)
    { SecretOpens++; Assert.Equal(Binding,binding);return native?new WindowsOkxCredentialStore(Root).Open(binding,paths):new(binding,"DemoTradingApi",Key.ToArray(),Secret.ToArray(),Pass.ToArray()); }
    public ValueTask<bool> ConfirmAsync(OkxActivationRequest request,CancellationToken ct)=>ActivationOverride?.Invoke(request,ct)??ValueTask.FromResult(true);
    public ValueTask<OkxKeyScopeInspection?> InspectAsync(OkxScopeBinding binding,string fingerprint,CancellationToken ct)
        =>BaselineOverride?.InspectAsync(binding,fingerprint,ct)??ValueTask.FromResult<OkxKeyScopeInspection?>(new(fingerprint,"okx-dedicated-cash-no-funding-v1","synthetic-inspection","192.0.2.1"));
    public ValueTask<bool> ApproveAsync(NavalBaselineApprovalRequest request,CancellationToken ct)
    { Approvals++; Assert.Equal(1234.56m,request.Cash);Assert.Equal(Binding.Account,request.Account);OnApproval?.Invoke();return BaselineOverride?.ApproveAsync(request,ct)??ValueTask.FromResult(true); }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Assert.Equal("eea.okx.com",request.RequestUri!.Host);Assert.Equal(HttpMethod.Get,request.Method);
        var path=request.RequestUri.PathAndQuery;Paths.Add(path);Methods.Add(request.Method);
        Assert.Equal("1",request.Headers.GetValues("x-simulated-trading").Single());
        if(path!="/api/v5/public/time")
        {
            var stamp=request.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
            Assert.Equal(Convert.ToBase64String(HMACSHA256.HashData(Secret,Encoding.ASCII.GetBytes(stamp+"GET"+path))),request.Headers.GetValues("OK-ACCESS-SIGN").Single());
        }
        string name=path.Split('?')[0];JsonNode body;
        if(name.StartsWith("/api/v5/trade/",StringComparison.Ordinal)||name.StartsWith("/api/v5/account/bills",StringComparison.Ordinal))body=JsonNode.Parse("{\"code\":\"0\",\"msg\":\"\",\"data\":[]}")!;
        else if(name=="/api/v5/account/interest-limits")body=JsonNode.Parse("{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"debt\":\"0\",\"interest\":\"0\",\"records\":[{\"ccy\":\"USDC\",\"usedLmt\":\"0\",\"interest\":\"0\",\"interestFreeLiab\":\"0\",\"potentialBorrowingAmt\":\"0\"}]}]}")!;
        else
        {
            string oracleName=name switch {"/api/v5/account/config"=>"account","/api/v5/account/balance"=>"balance","/api/v5/account/instruments"=>"instruments","/api/v5/account/trade-fee"=>"fees",_=>"time"};
            var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir!=null&&!Directory.Exists(Path.Combine(dir.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures")))dir=dir.Parent;
            var oracle=JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/baseline-schema.json")))!;
            body=oracle["endpoints"]![oracleName]!["control"]!.DeepClone();
            var row=body["data"]![0]!;string now=Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString();
            if(oracleName=="time")row["ts"]=now;
            if(oracleName=="account") {row["uid"]=Binding.Account.AccountId;row["mainUid"]=Binding.MainUid;row["acctLv"]="1";row["perm"]="read_only,trade";row["ip"]="192.0.2.1";row["feeType"]="1";row["autoLoan"]=false;row["enableSpotBorrow"]=false;row["spotBorrowAutoRepay"]=false;row["type"]="0";row["spotRoleType"]="0";}
            if(oracleName=="balance") {row["uTime"]=BalanceUtc;var detail=row["details"]![0]!.DeepClone();detail["ccy"]="USDC";foreach(var field in new[]{"cashBal","availBal","eq"})detail[field]="1234.56";foreach(var field in new[]{"frozenBal","ordFrozen","liab","interest","crossLiab","isoLiab","stgyEq","fixedBal"})detail[field]="0";detail["uTime"]=BalanceUtc;row["details"]=new JsonArray(detail);}
            if(oracleName=="instruments") {row["instId"]="BTC-USDC";row["baseCcy"]="BTC";row["quoteCcy"]="USDC";row["tradeQuoteCcyList"]=new JsonArray("USDC");row["state"]="live";}
            if(oracleName=="fees"){row["ts"]=now;row["feeGroup"]![0]!["groupId"]="4";}
        }
        Mutate?.Invoke(name,body);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json")});
    }
    public async ValueTask DisposeAsync(){await Service.DisposeAsync();await Runtime.DisposeAsync();Dispose();if(!preserve)Directory.Delete(Root,true);}
}
internal sealed class OperationalClock : TimeProvider
{
    private readonly DateTimeOffset origin=DateTimeOffset.UtcNow;
    private readonly global::System.Diagnostics.Stopwatch elapsed=global::System.Diagnostics.Stopwatch.StartNew();
    private long wallOffset,monoOffset;
    internal bool UseSystemUtc;
    public override DateTimeOffset GetUtcNow()=>UseSystemUtc?DateTimeOffset.UtcNow:origin+elapsed.Elapsed+TimeSpan.FromTicks(Interlocked.Read(ref wallOffset));
    public override long GetTimestamp()=>elapsed.Elapsed.Ticks+Interlocked.Read(ref monoOffset);
    public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    internal void Advance(TimeSpan t,bool wall=true,bool monotonic=true){if(wall)Interlocked.Add(ref wallOffset,t.Ticks);if(monotonic)Interlocked.Add(ref monoOffset,t.Ticks);}
}
internal sealed class OperationalSocket : WebSocket
{
    private readonly Channel<byte[]> replies=Channel.CreateUnbounded<byte[]>();private readonly Uri uri;private WebSocketState state=WebSocketState.Open;
    internal OperationalSocket(Uri uri){this.uri=uri;Assert.Equal("wseeapap.okx.com",uri.Host);}
    public override WebSocketCloseStatus? CloseStatus=>null;public override string? CloseStatusDescription=>null;public override WebSocketState State=>state;public override string? SubProtocol=>null;
    public override Task SendAsync(ArraySegment<byte> b,WebSocketMessageType type,bool end,CancellationToken ct)
    {
        string text=Encoding.UTF8.GetString(b);string reply;
        if(text=="ping")reply="pong";
        else {var n=JsonNode.Parse(text)!;if(n["op"]!.GetValue<string>()=="login"){var a=n["args"]![0]!;string stamp=a["timestamp"]!.GetValue<string>();Assert.Equal(Convert.ToBase64String(HMACSHA256.HashData(OperationalPeer.Secret,Encoding.ASCII.GetBytes(stamp+"GET/users/self/verify"))),a["sign"]!.GetValue<string>());reply="{\"event\":\"login\",\"code\":\"0\",\"msg\":\"\",\"connId\":\"peer\"}";}else {string channel=uri.AbsolutePath.EndsWith("private",StringComparison.Ordinal)?"orders":"orders-algo";Assert.Equal(channel,n["args"]![0]!["channel"]!.GetValue<string>());reply="{\"id\":\""+n["id"]!.GetValue<string>()+"\",\"event\":\"subscribe\",\"arg\":{\"channel\":\""+channel+"\",\"instType\":\"SPOT\"},\"connId\":\"peer\"}";}}
        replies.Writer.TryWrite(Encoding.UTF8.GetBytes(reply));return Task.CompletedTask;
    }
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b,CancellationToken ct){var bytes=await replies.Reader.ReadAsync(ct);bytes.AsSpan().CopyTo(b.AsSpan());return new(bytes.Length,WebSocketMessageType.Text,true);}
    public override void Abort(){state=WebSocketState.Aborted;}public override void Dispose(){state=WebSocketState.Closed;}
    public override Task CloseAsync(WebSocketCloseStatus s,string? d,CancellationToken ct){Dispose();return Task.CompletedTask;}public override Task CloseOutputAsync(WebSocketCloseStatus s,string? d,CancellationToken ct)=>CloseAsync(s,d,ct);
}
