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
    internal const string ProductionBlocker="PRODUCTION_PROTECTION_PROFILE_UNAPPROVED";
    private readonly OkxNavalRuntime runtime;
    private readonly ConfigStore config;
    private readonly SemaphoreSlim actionLane=new(1,1);
    private readonly ConcurrentDictionary<string,Entry> entries=new(StringComparer.Ordinal);
    private readonly object sync=new();
    private readonly HashSet<string> commands=new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime=new();
    private CancellationTokenSource? pending;
    private long generation;
    private string last="NOT_CONNECTED";
    private bool disposed;
    private sealed class Entry(OkxAccountConnectionService account)
    {
        internal readonly OkxAccountConnectionService Account=account;
        internal OkxOperationalTradingService? Trading;
        internal CancellationTokenSource? LoopCancellation;
        internal Task? Loop;
    }
    internal OkxOperatorControl(OkxNavalRuntime runtime,ConfigStore config){this.runtime=runtime;this.config=config;}
    internal object ReadStatus()=>new {mode="STOP",productionProfileApproved=false,productionBlocker=ProductionBlocker,
        nativeAvailable=OperatingSystem.IsWindows()&&Environment.UserInteractive,lastResult=last,
        requirements=new[]{"Przejrzany profil ochrony FOK + net-base-fee SL/TP","Brak luki po anulowaniu ochrony przed sprzedażą","Udowodniona finalność opłat/rozliczenia","Prywatne DEMO wymaga osobnej zgody i lokalnego testu"},
        accounts=runtime.Bindings.Select(b=>
        {
            entries.TryGetValue(b.ScopeId,out var entry);var trading=entry?.Trading?.ReadStatus();
            return new {scopeId=b.ScopeId,environment=b.Account.Environment.ToString(),uid=b.Account.AccountId,quote=b.Account.QuoteCurrency,
                connected=entry?.Account.IsConnected??false,mode=trading?.Mode??"STOP",code=trading?.Code??entry?.Account.Status??"NOT_CONNECTED",
                armed=trading?.Armed??false,ready=trading?.Ready??false,revision=trading?.Revision,
                cash=trading?.Cash.ToString(CultureInfo.InvariantCulture),held=trading?.Held.ToString(CultureInfo.InvariantCulture),
                obligations=trading?.Obligations??Array.Empty<string>(),observations=entry?.Account.ReadAccountStatus()};
        }).ToArray()};
    internal bool Known(string scope)=>runtime.Bindings.Any(b=>b.ScopeId==scope);
    private bool Register(string scope,string id)
    {
        lock(sync){if(disposed||commands.Count>=512||!commands.Add(scope+":"+id))return false;return true;}
    }
    internal async Task<string> ExecuteAsync(string scope,string action,string commandId,CancellationToken ct)
    {
        if(!Register(scope,commandId))return "COMMAND_ALREADY_SEEN_OR_LIMIT";
        // STOP/revocation is never queued behind a native dialog, connection or scheduler.
        if(action is "stop" or "emergency" or "disconnect")
        {
            lock(sync){generation++;pending?.Cancel();runtime.Activation.Revoke();}
            if(entries.TryGetValue(scope,out var existing))
            {
                existing.Account.RevokePermissions();
                if(action=="emergency")existing.Trading?.EmergencyStop();
                if(action=="disconnect")
                {
                    await actionLane.WaitAsync(ct).ConfigureAwait(false);
                    try{await CloseEntry(scope).ConfigureAwait(false);}finally{actionLane.Release();}
                }
            }
            return last=action=="emergency"?"EMERGENCY_STOP_LATCHED_CHECK_OBLIGATIONS":action=="disconnect"?"DISCONNECTED_STOP":"OPERATOR_STOP";
        }
        if(!await actionLane.WaitAsync(0,ct).ConfigureAwait(false))return "LOCAL_ACTION_PENDING";
        try
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
            long epoch;lock(sync){pending=linked;epoch=generation;}
            if(action=="configure")
            {
                if(entries.Values.Any(e=>e.Account.IsConnected))return last="DISCONNECT_BEFORE_CONFIGURATION";
                var added=await WindowsOkxProvisioning.CreateAsync(config,linked.Token).ConfigureAwait(false);
                if(added==null)return last="NATIVE_CANCELLED";
                runtime.AddProvisionedBinding(added);return last="CONFIGURED_STOP_NOT_CONNECTED";
            }
            var binding=runtime.Bindings.Single(b=>b.ScopeId==scope);
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
                if(!await current.Account.ApproveAndCreateAsync(linked.Token).ConfigureAwait(false))return last="BASELINE_REFUSED_PRESERVED";
                StartScheduler(current);return last=current.Account.Status;
            }
            if(current.Trading==null)return last="BASELINE_REQUIRED";
            if(action=="arm")
            {
                var request=new OkxActivationRequest(binding,runtime.CredentialFingerprint(binding),current.Account.TradingSessionId,epoch,OkxActivationPurpose.Execute,DateTimeOffset.UtcNow.AddSeconds(120));
                var permission=await runtime.Activation.RequestAsync(request,linked.Token).ConfigureAwait(false);
                if(permission==null)return last="EXECUTE_CANCELLED_OR_EXPIRED";
                return last=current.Trading.Arm(request,permission)?"EXECUTE_ARMED_START_SEPARATE_PROFILE_STILL_REQUIRED":"EXECUTE_REFUSED";
            }
            if(action=="start")return last=current.Trading.Start().Code;
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
        entry.Trading=OkxOperationalTradingService.Create(entry.Account);
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
