using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Desktop.Services;

namespace Apex.Desktop.Okx;

// One control owner; each scope uses the accepted account service and its exact trading owner.
// Construction is passive: no credentials, network, account owner or timer is opened here.
internal sealed class OkxOperatorControl : IAsyncDisposable
{
    internal const string ProductionBlocker="ACCOUNT_CONFIGURATION_REQUIRED";
    private readonly OkxNavalRuntime runtime;
    private readonly ConfigStore config;
    private readonly Func<OkxAccountConnectionService,OkxOperationalTradingService> tradingFactory;
    private readonly SemaphoreSlim actionLane=new(1,1);
    private readonly ConcurrentDictionary<string,Entry> entries=new(StringComparer.Ordinal);
    private readonly object sync=new();
    private readonly HashSet<string> commands=new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime=new();
    private CancellationTokenSource? pending;
    private long generation;
    private string last="NOT_CONNECTED";
    private string enrollmentStage="NOT_STARTED";
    private readonly ConcurrentDictionary<string,OkxDemoBalanceObservation> demoReads=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,OkxReadAccount> readAccounts=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,Apex.Adapters.Okx.Execution.OkxEnrollmentObservation> readObservations=new(StringComparer.Ordinal);
    private bool disposed;
    private sealed class Entry(OkxAccountConnectionService account)
    {
        internal readonly OkxAccountConnectionService Account=account;
        internal OkxOperationalTradingService? Trading;
        internal CancellationTokenSource? LoopCancellation;
        internal Task? Loop;
    }
    internal OkxOperatorControl(OkxNavalRuntime runtime,ConfigStore config,Func<OkxAccountConnectionService,OkxOperationalTradingService>? tradingFactory=null){this.runtime=runtime;this.config=config;this.tradingFactory=tradingFactory??(a=>OkxOperationalTradingService.Create(a));if(OperatingSystem.IsWindows())foreach(var a in new WindowsOkxReadStore(config.DataDir).List())readAccounts.TryAdd(a.ScopeId,a);}
    internal object ReadStatus()=>new {mode=entries.Values.Any(e=>e.Trading?.ReadStatus().Mode=="RUNNING")?"RUNNING":"STOP",productionProfileApproved=entries.Values.Any(e=>e.Trading?.ReadStatus().ProductionProfileApproved==true),productionBlocker=entries.Count==0?ProductionBlocker:null,conditionalPolicyAvailable=true,
        nativeAvailable=OperatingSystem.IsWindows()&&Environment.UserInteractive,lastResult=last,enrollmentStage,unboundStoreState=WindowsOkxProvisioning.PassiveUnboundState(config),
        requirements=new[]{"Dedykowany cash SPOT ≤1x, bez pożyczek; feeType=0: kupno fee w base, sprzedaż w quote; feeType=1: quote. Rzeczywiste fee i inventory netto są wiążące.","FOK z żądanym TP i SL; ochrona warunkowa po fill, możliwy brak ochrony i niechroniona reszta po anulowaniu", "Osobne uzbrojenie i START; STOP nie oznacza sprzedaży", "Katalog prywatny, świeże referencje USD/USDT do limitów; prywatny test DEMO pozostaje lokalnym działaniem użytkownika"},
        accounts=runtime.Bindings.Select(b=>
        {
            entries.TryGetValue(b.ScopeId,out var entry);var trading=entry?.Trading?.ReadStatus();
            return new {scopeId=b.ScopeId,environment=b.Account.Environment.ToString(),uid=b.Account.AccountId,quote=b.Account.QuoteCurrency,
                connected=entry?.Account.IsConnected??false,mode=trading?.Mode??"STOP",code=trading?.Code??entry?.Account.Status??"NOT_CONNECTED",
                armed=trading?.Armed??false,ready=trading?.Ready??false,revision=trading?.Revision,
                cash=trading?.Cash.ToString(CultureInfo.InvariantCulture),held=trading?.Held.ToString(CultureInfo.InvariantCulture),
                obligations=trading?.Obligations,inventory=trading?.Inventory,conditionalPolicy=trading?.ProductionProfileApproved??false,obligationsKnown=trading!=null,observations=entry?.Account.ReadAccountStatus(),keyState=WindowsOkxProvisioning.PassiveKeyState(config.DataDir,b),demoRead=demoReads.TryGetValue(b.ScopeId,out var read)?read:null};
        }).Cast<object>().Concat(readAccounts.Values.Select(a=>
        {
            readObservations.TryGetValue(a.ScopeId,out var o);bool fresh=o!=null&&DateTimeOffset.UtcNow>=o.ObservedUtc&&DateTimeOffset.UtcNow-o.ObservedUtc<TimeSpan.FromMinutes(1);
            return (object)new{scopeId=a.ScopeId,environment="OkxDemo",uid=a.Uid,mainUid=a.MainUid,quote=(string?)null,connected=fresh,mode="STOP",armed=false,ready=false,readOnly=true,code=fresh?"CONNECTED_READ_ONLY_STOP":o==null?"SAVED_DISCONNECTED_RECONNECT_REQUIRED":"OBSERVATION_STALE_RECONNECT_REQUIRED",observedUtc=o?.ObservedUtc,
                balances=o?.Balances.Select(b=>new{currency=b.Currency,cash=b.Cash.Value?.ToString(CultureInfo.InvariantCulture),available=b.Available.Value?.ToString(CultureInfo.InvariantCulture)}).ToArray(),
                markets=o?.Instruments.Select(i=>new{instrument=i.Instrument,catalogQuote=i.CatalogQuote,tradeQuotes=i.TradeQuoteCurrencies,state=i.State}).ToArray(),obligationsKnown=false,cash=(string?)null,held=(string?)null};
        })).ToArray()};
    internal bool IsDemo(string scope)=>readAccounts.ContainsKey(scope)||runtime.Bindings.Any(b=>b.ScopeId==scope&&b.Account.Environment==Apex.Core.Naval.NavalEnvironment.OkxDemo);
    internal bool Known(string scope)=>readAccounts.ContainsKey(scope)||runtime.Bindings.Any(b=>b.ScopeId==scope);
    private bool Register(string scope,string id)
    {
        lock(sync){if(disposed||commands.Count>=512||!commands.Add(scope+":"+id))return false;return true;}
    }
    internal async Task<string> ExecuteAsync(string scope,string action,string commandId,CancellationToken ct,string? reservationId=null)
    {
        // STOP/revocation is never queued behind a native dialog, connection or scheduler.
        if(action is "stop" or "emergency" or "disconnect" or "cancel")
        {
            lock(sync){generation++;pending?.Cancel();runtime.Activation.Revoke();}
            readObservations.TryRemove(scope,out _);
            bool ownerOpen=entries.TryGetValue(scope,out var existing)&&existing.Account.BrokerSession!=null;
            if(existing!=null)
            {
                existing.Account.RevokePermissions();
                if(action=="emergency")existing.Trading?.EmergencyStop();
                if(action=="disconnect")
                {
                    await actionLane.WaitAsync(ct).ConfigureAwait(false);
                    try{await CloseEntry(scope).ConfigureAwait(false);}finally{actionLane.Release();}
                }
            }
            return last=action=="emergency"?(ownerOpen?"EMERGENCY_STOP_LATCHED_CHECK_OBLIGATIONS":"EMERGENCY_STOP_NO_OPEN_OWNER"):action=="disconnect"?"DISCONNECTED_STOP":"OPERATOR_STOP";
        }
        if(!Register(scope,commandId))return "COMMAND_ALREADY_SEEN_OR_LIMIT";
        if(!await actionLane.WaitAsync(0,ct).ConfigureAwait(false))return "LOCAL_ACTION_PENDING";
        try
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
            long epoch;lock(sync){pending=linked;epoch=generation;}
            if(action=="configure")
            {
                if(entries.Values.Any(e=>e.Account.IsConnected))return last="DISCONNECT_BEFORE_CONFIGURATION";
                var added=await WindowsOkxSimpleConnect.CreateAsync(config,linked.Token,sync,()=>generation==epoch&&!disposed,a=>readAccounts.TryAdd(a.ScopeId,a),s=>enrollmentStage=s).ConfigureAwait(false);
                enrollmentStage=added.Code;
                if(added.Account==null)return last=added.Code;
                readAccounts.TryAdd(added.Account.ScopeId,added.Account);
                lock(sync){if(generation!=epoch||linked.IsCancellationRequested||added.Observation==null)return last=enrollmentStage="CONNECT_CANCELLED_SAVED_DISCONNECTED";readObservations[added.Account.ScopeId]=added.Observation;}
                return last=enrollmentStage=added.Code;
            }
            if(readAccounts.TryGetValue(scope,out var readAccount))
            {
                if(action is not ("reconnect" or "test-demo" or "diagnose"))return last="READ_ONLY_NO_ACTIVATION";
                using var saved=new WindowsOkxReadStore(config.DataDir).Open(scope);
                if(saved.Account!=readAccount)return last="READ_ACCOUNT_BINDING_MISMATCH";
                if(action=="diagnose")return last="SAVED_READ_ONLY_KEY_VERIFIED";
                readObservations.TryRemove(scope,out _);
                var observation=await Apex.Adapters.Okx.Execution.OkxEnrollmentDiscoveryClient.DiscoverAsync(saved.Account.Environment,saved.Account.Provenance,saved.Values[0],saved.Values[1],saved.Values[2],saved.Account.Uid,saved.Account.MainUid,linked.Token).ConfigureAwait(false);
                lock(sync){if(generation!=epoch||linked.IsCancellationRequested)return last="CONNECT_CANCELLED";if(observation.Observation is {} o&&o.Fingerprint==readAccount.Fingerprint)readObservations[scope]=o;}
                return last=observation.Code;
            }
            var binding=runtime.Bindings.Single(b=>b.ScopeId==scope);
            if(action=="attach-local")
            {
                if(entries.Values.Any(e=>e.Account.IsConnected)||binding.Account.Environment!=Apex.Core.Naval.NavalEnvironment.OkxDemo)return last="LOCAL_ATTACH_DISCONNECTED_DEMO_REQUIRED";
                return last=await runtime.AttachWindowsExistingAsync(binding,epoch,linked.Token,sync,()=>generation==epoch&&!disposed).ConfigureAwait(false)?"LOCAL_EXISTING_DEMO_ATTACHED_STOP":"LOCAL_ATTACH_REFUSED_PRESERVED";
            }
            if(action=="diagnose")
            {
                string state=WindowsOkxProvisioning.PassiveKeyState(config.DataDir,binding);
                if(state!="CIPHERTEXT_PRESENT_NOT_VERIFIED")return last=state;
                return last=WindowsOkxProvisioning.SavedBindingMatches(config,binding)&&runtime.VerifySavedCredential(binding)?"SAVED_KEY_CONFIG_RUNTIME_VERIFIED":"SAVED_BINDING_REFUSED_PRESERVED";
            }
            if(action=="test-demo")
            {
                if(binding.Account.Environment!=Apex.Core.Naval.NavalEnvironment.OkxDemo)return last="DEMO_TEST_LIVE_DENIED";
                if(entries.Values.Any(e=>e.Account.IsConnected||e.Trading!=null))return last="DEMO_TEST_DISCONNECT_FIRST";
                demoReads.TryRemove(scope,out _);
                if(!WindowsOkxProvisioning.SavedBindingMatches(config,binding))return last="DEMO_TEST_CONFIG_BINDING_MISMATCH";
                var observation=await OkxDemoBalanceTest.RunAsync(binding,()=>runtime.OpenDemoReadCredential(binding),linked.Token).ConfigureAwait(false);
                lock(sync){if(generation!=epoch||linked.IsCancellationRequested)return last="DEMO_TEST_CANCELLED";demoReads[scope]=observation;}
                return last=observation.Code;
            }
            if(action=="connect")
            {
                if(entries.Values.Any(e=>e.Account.IsConnected))return last="DISCONNECT_BEFORE_ACCOUNT_CHANGE";
                await CloseEntry(scope).ConfigureAwait(false);
                var entry=new Entry(runtime.AccountService(scope));entries[scope]=entry;
                string fingerprint=runtime.CredentialFingerprint(binding);
                var request=new OkxActivationRequest(binding,fingerprint,Guid.NewGuid(),epoch,OkxActivationPurpose.Observe,DateTimeOffset.UtcNow.AddSeconds(120));
                var permission=await runtime.Activation.RequestAsync(request,linked.Token).ConfigureAwait(false);
                if(permission==null)return last="OBSERVE_CANCELLED_OR_EXPIRED";
                if(!await entry.Account.ConnectAsync(request,permission,linked.Token).ConfigureAwait(false))return last=entry.Account.Status;
                if(entry.Account.BrokerSession!=null)StartScheduler(entry);
                return last=entry.Account.Status;
            }
            if(!entries.TryGetValue(scope,out var current))return last="CONNECT_REQUIRED";
            if(action=="baseline")
            {
                if(!await current.Account.ApproveAndCreateAsync(linked.Token,refresh:true).ConfigureAwait(false))return last="BASELINE_REFUSED_PRESERVED";
                StartScheduler(current);return last=current.Account.Status;
            }
            if(current.Trading==null)return last="BASELINE_REQUIRED";
            if(action=="arm")
            {
                current.Trading.Stop();runtime.Activation.Revoke(); // Replacing/canceling permission never retains an older arm.
                var request=new OkxActivationRequest(binding,runtime.CredentialFingerprint(binding),current.Account.TradingSessionId,epoch,OkxActivationPurpose.Execute,DateTimeOffset.UtcNow.AddSeconds(120));
                var permission=await runtime.Activation.RequestAsync(request,linked.Token).ConfigureAwait(false);
                if(permission==null)return last="EXECUTE_CANCELLED_OR_EXPIRED";
                return last=current.Trading.Arm(request,permission)?"EXECUTE_ARMED_START_SEPARATE_CONDITIONAL_RISK":"EXECUTE_REFUSED";
            }
            if(action=="start")return last=current.Trading.Start().Code;
            if(action=="recover")return last=reservationId==null?"EXACT_RESERVATION_REQUIRED":(await current.Trading.ExitAsync(reservationId,linked.Token).ConfigureAwait(false)).Code;
            return last="ACTION_DENIED";
        }
        catch(OperationCanceledException){return last="LOCAL_ACTION_CANCELLED";}
        catch(Exception e) when(e is not OutOfMemoryException)
        {
            // No raw exception/private response data crosses this boundary.
            return last=OperatingSystem.IsWindows()?"LOCAL_ACTION_REFUSED_PRESERVED":"NATIVE_WINDOWS_REQUIRED";
        }
        finally{lock(sync)pending=null;actionLane.Release();}
    }
    private void StartScheduler(Entry entry)
    {
        if(entry.Trading!=null)return;
        entry.Trading=tradingFactory(entry.Account);
        entry.LoopCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        entry.Loop=Schedule(entry,entry.LoopCancellation.Token);
    }
    private async Task Schedule(Entry entry,CancellationToken ct)
    {
        try{await entry.Trading!.RunAsync(ct).ConfigureAwait(false);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){}
        catch(Exception e) when(e is not OutOfMemoryException){entry.Trading!.Stop();last="SCHEDULER_STOPPED_RECONNECT_REQUIRED";}
    }
    private async Task CloseEntry(string scope)
    {
        if(!entries.TryRemove(scope,out var entry))return;
        entry.LoopCancellation?.Cancel();entry.Account.RevokePermissions();
        if(entry.Loop!=null)await entry.Loop.ConfigureAwait(false);
        await entry.Account.DisposeAsync().ConfigureAwait(false);entry.LoopCancellation?.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        lock(sync){if(disposed)return;disposed=true;generation++;pending?.Cancel();runtime.Activation.Revoke();lifetime.Cancel();}
        await actionLane.WaitAsync().ConfigureAwait(false);
        try{foreach(var scope in entries.Keys)await CloseEntry(scope).ConfigureAwait(false);}finally{actionLane.Release();lifetime.Dispose();}
    }
}
