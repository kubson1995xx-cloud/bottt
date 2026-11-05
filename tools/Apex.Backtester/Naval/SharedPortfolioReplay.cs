using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apex.Core.Naval;
namespace Apex.Backtester.Naval;

// Synthetic instructions are not provider evidence, and cannot construct DEMO/LIVE authority.
internal sealed class SharedPortfolioReplay : IDisposable
{
    internal const string Classification="OFFLINE_SHARED_CAPITAL / SYNTHETIC_NOT_RESEARCH / NO_BROKER_AUTHORITY";
    private readonly SharedReplayInput _input;
    private readonly NavalPortfolioDefinition _definition;
    private readonly string _directory;
    private readonly ReplayClock _clock;
    private NavalCoreStateOwner _owner=null!;
    private NavalPortfolioCoordinator _coordinator=null!;
    private long _generation,_receipt;
    private Dictionary<string,decimal> _marks;
    internal SharedPortfolioReplay(SharedReplayInput input,string directory)
    {
        _input=input;_directory=directory;_clock=new ReplayClock{UtcNow=input.ClockUtc};_marks=new(input.Marks);
        if(input.Schema!="naval-shared-replay-v1"||input.ClockUtc==default||input.Steps.Count>10000)throw new ArgumentException("Unsupported shared replay schema/clock/size");
        if(input.Steps.Any(s=>!new[]{"reconcile","start","reserve","confirm","reject","terminal","timeout","restart","advance","stop"}.Contains(s.Op)))throw new ArgumentException("Unknown controlled replay operation");
        var signals=input.Signals.Select(s=>new Apex.Core.Naval.NavalSignal(s.SignalId,input.Members.Single(m=>m.Account.AccountId==s.AccountId).Account,
            s.Fleet,s.Instrument,s.RuleIdentity,s.EarliestUtc,s.ExpiresUtc)).ToArray();
        _definition=new(input.Portfolio,input.Limits,input.Members,input.Catalog,input.Fees,signals);
        NavalCashRiskPolicy.ValidateDefinition(_definition); // Before directory/store mutation, including enum/environment checks.
        Open();
    }
    private void Open()
    {
        _owner=new(_directory,new OfflineNavalStore(_directory),_definition);
        _coordinator=new(_owner,_definition,_clock);_generation=checked(_owner.Read().Naval!.Generation+1);_receipt=0;
    }
    internal object Run()
    {
        var initial=_coordinator.Snapshot();var output=new List<object>();
        foreach(var step in _input.Steps)
        {
            NavalRiskResult? result=null;
            if(step.Marks!=null)_marks=new(step.Marks);
            switch(step.Op)
            {
                case "advance": if(step.Seconds<=0)throw new ArgumentException("advance requires positive seconds");_clock.UtcNow=_clock.UtcNow.AddSeconds(step.Seconds);break;
                case "restart": _owner.Dispose();Open();break;
                case "stop": result=_coordinator.Stop("EXPLICIT_SYNTHETIC_STOP");break;
                case "start": var state=_owner.Read().Naval!;result=_coordinator.Start(new(_coordinator.OwnerToken,state.Revision,_generation,true));break;
                case "reserve":
                    var account=_definition.Members.Single(m=>m.Account.AccountId==step.AccountId).Account;
                    if(!step.Fleet.HasValue)throw new ArgumentException("reserve requires fleet");
                    result=_coordinator.ForFleet(account,step.Fleet.Value).TryReserve(new(step.CommandId,step.SignalId,step.Instrument,
                        step.Quantity,step.EntryPriceCeiling,step.StopPrice,step.TakeProfitPrice));break;
                case "timeout": result=_coordinator.MarkUncertain(Find(step.ReferenceCommand).Id,"CONTROLLED_TIMEOUT");break;
                case "reconcile": result=_coordinator.Reconcile(Issue(step,NavalEvidenceKind.Reconcile));break;
                case "confirm": result=_coordinator.Confirm(Issue(step,NavalEvidenceKind.Confirm));break;
                case "reject": result=_coordinator.Reject(Issue(step,NavalEvidenceKind.Reject));break;
                case "terminal": result=_coordinator.Terminal(Issue(step,NavalEvidenceKind.Terminal));break;
            }
            output.Add(new{op=step.Op,result,view=_coordinator.Snapshot()});
            if(result?.Status==NavalMutationStatus.Stopped&&result.Code=="STORAGE_FROZEN")throw new IOException("Ambiguous persistence; reopen authoritative state, no in-process retries");
        }
        return new{classification=Classification,configIdentity=_definition.Digest,initial,steps=output,final=_coordinator.Snapshot(),
            authority="one EngineState.Naval; synthetic complete observations echo the durable owner; no external broker reconciliation claimed"};
    }
    internal NavalReadPublication? ReadPublication()=>_coordinator.ReadPublication();
    internal DateTimeOffset SourceClockUtc=>_clock.UtcNow;
    private NavalReservation Find(string command)=>_owner.Read().Naval!.Reservations.Single(r=>r.Intent.CommandId==command);
    private NavalEvidenceReceipt Issue(SharedReplayStep step,NavalEvidenceKind kind)
    {
        var s=_owner.Read().Naval!;
        var fills=step.Fills.Select(f=>{var r=Find(f.ReferenceCommand);return new NavalFill(r.Account,r.Intent.Instrument,f.TradeId,r.Id,
            f.Reduction,f.Quantity,f.Price,f.Fee,f.FeeCurrency,f.TradeUtc);}).ToArray();
        bool final=kind==NavalEvidenceKind.Reject||kind==NavalEvidenceKind.Terminal;
        bool complete=kind==NavalEvidenceKind.Reconcile||final;
        var terminal=final?new[]{Find(step.ReferenceCommand).Id}:Array.Empty<string>();
        // These are explicit controlled simulation observations, not an independent money ledger.
        var observations=step.Observations?.Select(o=>new NavalObservation(Find(o.ReferenceCommand).Id,o.Inventory,o.Pending)).ToArray()
            ??s.Reservations.Where(NavalCashRiskPolicy.Active).Select(r=>new NavalObservation(r.Id,r.Inventory,terminal.Contains(r.Id)?0:r.Pending)).ToArray();
        var cash=step.Cash?.Select(a=>{var existing=s.Accounts.Single(c=>c.Account.AccountId==a.AccountId);return existing with{Cash=a.Cash};}).ToArray()??s.Accounts;
        return new(_coordinator.OwnerToken,_definition.Id,"synthetic-"+_generation+"-"+(++_receipt),_generation,
            _generation>s.Generation?1:s.Sequence+1,step.CaptureUtc??_clock.UtcNow,_clock.UtcNow,kind,complete,final,_marks,fills,cash,observations,terminal);
    }
    public void Dispose()=>_owner?.Dispose();
    private sealed class ReplayClock : INavalClock { public DateTimeOffset UtcNow { get; set; } }
}
internal sealed class SharedReplayInput
{
    public string Schema { get; set; }="";
    public NavalPortfolioId Portfolio { get; set; }=null!;
    public NavalCashLimits Limits { get; set; }=new();
    public List<NavalSeed> Members { get; set; }=new();
    public List<Apex.Core.Naval.NavalInstrument> Catalog { get; set; }=new();
    public NavalFeeSchedule Fees { get; set; }=new();
    public DateTimeOffset ClockUtc { get; set; }
    public Dictionary<string,decimal> Marks { get; set; }=new();
    public List<SharedReplaySignal> Signals { get; set; }=new();
    public List<SharedReplayStep> Steps { get; set; }=new();
}
internal sealed record SharedReplaySignal(string SignalId,string AccountId,NavalFleet Fleet,string Instrument,string RuleIdentity,DateTimeOffset EarliestUtc,DateTimeOffset ExpiresUtc);
internal sealed class SharedReplayStep
{
    public string Op { get; set; }="";
    public string AccountId { get; set; }="";
    public NavalFleet? Fleet { get; set; }
    public string CommandId { get; set; }="";
    public string SignalId { get; set; }="";
    public string Instrument { get; set; }="";
    public decimal Quantity { get; set; }
    public decimal EntryPriceCeiling { get; set; }
    public decimal StopPrice { get; set; }
    public decimal TakeProfitPrice { get; set; }
    public string ReferenceCommand { get; set; }="";
    public int Seconds { get; set; }
    public Dictionary<string,decimal>? Marks { get; set; }
    public DateTimeOffset? CaptureUtc { get; set; }
    public List<SharedReplayFill> Fills { get; set; }=new();
    public List<SharedReplayCash>? Cash { get; set; }
    public List<SharedReplayObservation>? Observations { get; set; }
}
internal sealed record SharedReplayFill(string ReferenceCommand,string TradeId,bool Reduction,decimal Quantity,decimal Price,decimal Fee,string FeeCurrency,DateTimeOffset TradeUtc);
internal sealed record SharedReplayCash(string AccountId,decimal Cash);
internal sealed record SharedReplayObservation(string ReferenceCommand,decimal Inventory,decimal Pending);
