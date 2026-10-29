using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Engine;
namespace Apex.Core.Naval;

public sealed class NavalPortfolioCoordinator
{
    private readonly INavalStateOwner _owner;
    private readonly NavalPortfolioDefinition _definition;
    private readonly INavalClock _clock;
    private readonly long _restartGeneration;
    private bool _ready, _started, _frozen;
    internal Guid OwnerToken { get; }=Guid.NewGuid();
    internal NavalPortfolioCoordinator(INavalStateOwner owner,NavalPortfolioDefinition definition,INavalClock clock)
    {
        _owner=owner;_definition=definition;_clock=clock;NavalCashRiskPolicy.ValidateDefinition(definition);
        lock(owner){var s=owner.Read().Naval??throw new InvalidOperationException("Unconfigured Naval scope");
            if(s.ConfigDigest!=definition.Digest||s.Id!=definition.Id)throw new InvalidOperationException("Wrong Naval scope");
            _restartGeneration=checked(s.Generation+1);}
    }
    public INavalFleetRisk ForFleet(NavalAccountId account,NavalFleet fleet)
    {
        if(!Enum.IsDefined(typeof(NavalFleet),fleet)||!_definition.Members.Any(m=>m.Account==account))throw new ArgumentException("Unfunded/foreign fleet identity");
        return new Handle(this,account,fleet);
    }
    private sealed class Handle : INavalFleetRisk
    {
        private readonly NavalPortfolioCoordinator _c;private readonly NavalAccountId _a;private readonly NavalFleet _f;
        internal Handle(NavalPortfolioCoordinator c,NavalAccountId a,NavalFleet f){_c=c;_a=a;_f=f;}
        public NavalRiskResult TryReserve(NavalEntryIntent intent)=>_c.Reserve(_a,_f,intent);
    }
    private bool Fresh(NavalPortfolioState s)=>_ready&&s.ReconciledUtc.HasValue&&_clock.UtcNow>=s.ReconciledUtc&&_clock.UtcNow-s.ReconciledUtc<=TimeSpan.FromSeconds(30);
    private static NavalRiskResult Result(NavalPortfolioState s,NavalMutationStatus status,string code,string? id=null)=>new(status,code,id,s.Revision);
    private NavalRiskResult Commit(EngineState envelope,NavalPortfolioState s,NavalMutationStatus status,string code,string? id=null)
    {
        if(_frozen)return Result(envelope.Naval!,NavalMutationStatus.Stopped,"STORAGE_FROZEN");
        long old=envelope.DurableRevision;envelope.DurableRevision=checked(old+1);envelope.Naval=s with{Revision=envelope.DurableRevision};
        var result=_owner.TryCommit(old,envelope);
        if(result!=NavalCommitResult.Committed){_frozen=true;_ready=false;_started=false;return new(NavalMutationStatus.Stopped,result==NavalCommitResult.Conflict?"COMMIT_CONFLICT":"STORAGE_FROZEN",id,old);}
        return new(status,code,id,envelope.DurableRevision);
    }
    private NavalRiskResult StopCandidate(EngineState e,string reason,bool emergency=false,string? conflict=null)
    {
        _ready=false;_started=false;var s=e.Naval!;
        return Commit(e,s with{Mode="STOP",Reason=reason,EmergencyLatch=s.EmergencyLatch||emergency,
            Conflicts=conflict==null?s.Conflicts:s.Conflicts.Append(conflict).ToArray()},NavalMutationStatus.Stopped,reason);
    }
    private NavalRiskResult Reserve(NavalAccountId account,NavalFleet fleet,NavalEntryIntent intent)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;
            if(_frozen)return Result(s,NavalMutationStatus.Stopped,"STORAGE_FROZEN");
            // Validate binding before touching either fleet's durable signal namespace.
            if(intent==null||!NavalCashRiskPolicy.Text(intent.CommandId)||!NavalCashRiskPolicy.Text(intent.SignalId)||!NavalCashRiskPolicy.Text(intent.Instrument))return Result(s,NavalMutationStatus.Refused,"MALFORMED_IDENTITY");
            var signal=_definition.Signals.SingleOrDefault(x=>x.SignalId==intent.SignalId);
            if(signal==null||signal.Account!=account||signal.Fleet!=fleet||signal.Instrument!=intent.Instrument)return Result(s,NavalMutationStatus.Refused,"SIGNAL_BINDING");
            var digest=NavalJson.Hash(new{account,fleet,intent,signal.RuleIdentity});
            var previous=s.Commands.SingleOrDefault(c=>c.CommandId==intent.CommandId);
            if(previous!=null)
            {
                if(previous.Digest!=digest)return StopCandidate(e,"COMMAND_CONFLICT",true,digest);
                return new(NavalMutationStatus.Duplicate,previous.Result.Code,previous.Result.ReservationId,s.Revision);
            }
            string? refusal=null;NavalReservation? reservation=null;
            try
            {
                s=NavalCashRiskPolicy.Refresh(s,_definition,_clock.UtcNow,false);
                if(s.Commands.Any(c=>c.SignalId==intent.SignalId))refusal="SIGNAL_CONSUMED";
                else if(!_started||s.Mode!="RUNNING"||!Fresh(s)||s.DailyLatch||s.DrawdownLatch||s.EmergencyLatch)refusal="NOT_RUNNING_FRESH";
                else if(_clock.UtcNow<signal.EarliestUtc||_clock.UtcNow>signal.ExpiresUtc)refusal="SIGNAL_TIME";
                else
                {
                    var i=_definition.Catalog.Single(x=>x.Instrument==intent.Instrument);
                    decimal q=checked(decimal.Floor(intent.Quantity/i.Quantum)*i.Quantum);
                    if(q<i.MinimumQuantity||intent.EntryPriceCeiling<=0||intent.StopPrice<=0||intent.StopPrice>=intent.EntryPriceCeiling||intent.TakeProfitPrice<=intent.EntryPriceCeiling||
                        intent.EntryPriceCeiling%i.TickSize!=0||intent.StopPrice%i.TickSize!=0||intent.TakeProfitPrice%i.TickSize!=0)refusal="INVALID_LONG_PROTECTION_OR_QUANTUM";
                    else if(!s.Marks.TryGetValue(intent.Instrument,out var mark)||mark>intent.EntryPriceCeiling)refusal="EXECUTABLE_PRICE";
                    else
                    {
                        reservation=NavalCashRiskPolicy.Holds(new NavalReservation{Id=NavalJson.Hash(new{_definition.Id,account,fleet,intent.CommandId}),Account=account,Fleet=fleet,Intent=intent,
                            Quantity=q,Pending=q,Stage=NavalReservationStage.Reserved},_definition,s.Marks);
                        refusal=NavalCashRiskPolicy.Admission(s,reservation,_definition);
                    }
                }
            }
            catch(OverflowException){refusal="DECIMAL_OVERFLOW";}
            var status=refusal==null?NavalMutationStatus.Applied:NavalMutationStatus.Refused;
            var id=refusal==null?reservation!.Id:null;var code=refusal??"RESERVED";
            var receipt=new NavalRiskResult(status,code,id,checked(s.Revision+1));
            s=s with{Commands=s.Commands.Append(new NavalCommand(intent.CommandId,intent.SignalId,digest,receipt)).ToArray(),
                Reservations=id==null?s.Reservations:s.Reservations.Append(reservation!).ToArray()};
            return Commit(e,s,status,code,id);
        }
    }
    public NavalPortfolioView Snapshot()
    {
        lock(_owner)
        {
            var s=_owner.Read().Naval!;bool fresh=Fresh(s)&&!_frozen&&!s.EmergencyLatch;
            var accounts=s.Accounts.Select(a=>{decimal held=s.Reservations.Where(r=>r.Account==a.Account).Sum(NavalCashRiskPolicy.Held);return new NavalAccountView(a.Account,a.Cash,held,a.Cash-held);}).ToArray();
            decimal? eq=fresh?s.LiquidationEquity:null;
            var fleets=new[]{NavalFleet.Alpha,NavalFleet.Bravo}.Select(fleet=>
            {
                var reservations=s.Reservations.Where(r=>r.Fleet==fleet).ToArray();var ids=reservations.Select(r=>r.Id).ToHashSet();
                var fills=s.Fills.Where(f=>ids.Contains(f.Fill.ReservationId)).Select(f=>f.Fill).ToArray();
                decimal cashflow=fills.Sum(f=>(f.Reduction?1:-1)*f.Quantity*f.Price-(f.FeeCurrency==f.Account.QuoteCurrency?f.Fee:0));
                decimal? value=fresh?reservations.Sum(r=>NavalCashRiskPolicy.NetSell(r.Inventory,s.Marks[r.Intent.Instrument],_definition.Fees)):null;
                return new NavalFleetView(fleet,cashflow,reservations.Sum(r=>r.AllocatedCost),value,value+cashflow,Array.AsReadOnly(reservations),Array.AsReadOnly(fills));
            }).ToArray();
            return new(s.Revision,_started&&!_frozen?s.Mode:"STOP",_frozen?"STORAGE_FROZEN":!_ready?"RESTART_RECONCILE_REQUIRED":s.Reason,
                fresh?"COHERENT_OFFLINE":"UNKNOWN",fresh&&!s.EmergencyLatch,accounts.Sum(a=>a.Cash),accounts.Sum(a=>a.Held),accounts.Sum(a=>a.Free),eq,
                eq.HasValue?Math.Max(0,s.DayStartEquity*_definition.Limits.DailyPct/100-Math.Max(0,s.DayStartEquity-eq.Value)-s.Reservations.Sum(r=>r.RiskHold)):null,
                eq.HasValue?Math.Max(0,s.PeakEquity*_definition.Limits.DrawdownPct/100-Math.Max(0,s.PeakEquity-eq.Value)-s.Reservations.Sum(r=>r.RiskHold)):null,
                s.Reservations.Count(NavalCashRiskPolicy.Active),s.DailyLatch,s.DrawdownLatch,s.EmergencyLatch,Array.AsReadOnly(accounts),Array.AsReadOnly(s.Reservations.ToArray()),Array.AsReadOnly(fleets));
        }
    }
    internal NavalRiskResult Reconcile(NavalEvidenceReceipt receipt)=>Evidence(receipt,NavalEvidenceKind.Reconcile);
    internal NavalRiskResult Confirm(NavalEvidenceReceipt receipt)=>Evidence(receipt,NavalEvidenceKind.Confirm);
    internal NavalRiskResult Reject(NavalEvidenceReceipt receipt)=>Evidence(receipt,NavalEvidenceKind.Reject);
    internal NavalRiskResult Terminal(NavalEvidenceReceipt receipt)=>Evidence(receipt,NavalEvidenceKind.Terminal);
    private static string Conflict(NavalEvidenceReceipt r)=>NavalJson.Serialize(new { r.Portfolio,r.ReceiptId,r.Generation,r.Sequence,r.CaptureUtc,r.ReceiveUtc,r.Kind,r.Complete,r.Final,r.Marks,r.Fills,r.Cash,r.Observations,r.TerminalIds });
    private NavalRiskResult Evidence(NavalEvidenceReceipt r,NavalEvidenceKind expected)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;
            if(_frozen)return Result(s,NavalMutationStatus.Stopped,"STORAGE_FROZEN");
            if(r==null||r.OwnerToken!=OwnerToken||r.Portfolio!=_definition.Id||r.Kind!=expected||!NavalCashRiskPolicy.Text(r.ReceiptId))return Result(s,NavalMutationStatus.Refused,"EVIDENCE_AUTHORITY_KIND");
            string key=r.Generation+":"+r.ReceiptId;var old=s.Receipts.SingleOrDefault(x=>x.Key==key);
            if(old!=null)return old.Digest==r.Digest?Result(s,NavalMutationStatus.Duplicate,"RECEIPT_DUPLICATE"):StopCandidate(e,"RECEIPT_CONFLICT",true,Conflict(r));
            bool newGeneration=r.Generation>s.Generation;
            if(r.Generation<_restartGeneration||r.Generation<s.Generation||r.Sequence<=0||
                (newGeneration?(expected!=NavalEvidenceKind.Reconcile||!r.Complete||r.Sequence!=1):r.Sequence!=s.Sequence+1)||
                r.CaptureUtc>r.ReceiveUtc||r.ReceiveUtc>_clock.UtcNow||_clock.UtcNow-r.CaptureUtc>TimeSpan.FromSeconds(30)||
                (s.ReconciledUtc.HasValue&&r.CaptureUtc<s.ReconciledUtc))return StopCandidate(e,"SOURCE_ORDER_OR_TIME");
            if((expected==NavalEvidenceKind.Reconcile&&!r.Complete)||((expected==NavalEvidenceKind.Reject||expected==NavalEvidenceKind.Terminal)&&(!r.Complete||!r.Final||r.TerminalIds.Count==0)))
                return StopCandidate(e,"FINAL_COVERAGE_REQUIRED");
            try
            {
                var candidate=s;
                foreach(var f in r.Fills)
                {
                    string fillKey=NavalCoreStateOwner.FillKey(f),digest=NavalJson.Hash(f);
                    var accepted=candidate.Fills.SingleOrDefault(x=>x.Key==fillKey);
                    if(accepted!=null){if(accepted.Digest!=digest)return StopCandidate(e,"FILL_CONFLICT",true,NavalJson.Serialize(r.Fills));continue;}
                    if(s.ReconciledUtc.HasValue&&f.TradeUtc<s.ReconciledUtc&&expected!=NavalEvidenceKind.Reconcile)
                        return StopCandidate(e,"LATE_FILL_RECONCILE_REQUIRED",false,Conflict(r));
                    var position=candidate.Reservations.SingleOrDefault(x=>x.Id==f.ReservationId);
                    if(position==null||position.Account!=f.Account||position.Intent.Instrument!=f.Instrument||f.Quantity<=0||f.Price<=0||f.Fee<0||
                        !NavalCashRiskPolicy.Text(f.TradeId)||f.TradeUtc>r.CaptureUtc||(!f.Reduction&&position.TerminalCoverage))return StopCandidate(e,"UNOWNED_FILL",true,Conflict(r));
                    var instrument=_definition.Catalog.Single(x=>x.Instrument==f.Instrument);var fees=_definition.Fees;
                    bool inBase=f.Reduction?fees.ExitInBase:fees.EntryInBase;
                    decimal rate=f.Reduction?fees.ExitRate:fees.EntryRate;
                    if(f.FeeCurrency!=(inBase?instrument.BaseCurrency:instrument.QuoteCurrency)||f.Fee>checked(f.Quantity*(inBase?1:f.Price)*rate))return StopCandidate(e,"FEE_OVERRUN",true,NavalJson.Serialize(r.Fills));
                    var cash=candidate.Accounts.Single(a=>a.Account==f.Account);NavalReservation changed;
                    if(f.Reduction)
                    {
                        decimal removed=checked(f.Quantity+(inBase?f.Fee:0));
                        if(removed>position.Inventory)return StopCandidate(e,"OVERSELL",true,NavalJson.Serialize(r.Fills));
                        decimal cost=position.AllocatedCost*(position.Inventory-removed)/position.Inventory;
                        changed=position with{Inventory=position.Inventory-removed,AllocatedCost=cost,ReductionUnsettled=true};
                        cash=cash with{Cash=checked(cash.Cash+f.Quantity*f.Price-(inBase?0:f.Fee))};
                    }
                    else
                    {
                        if(f.Quantity>position.Pending)return StopCandidate(e,"ENTRY_OVERRUN",true,NavalJson.Serialize(r.Fills));
                        decimal debit=checked(f.Quantity*f.Price+(inBase?0:f.Fee));
                        if(debit>cash.Cash)return StopCandidate(e,"CASH_OVERRUN",true,NavalJson.Serialize(r.Fills));
                        cash=cash with{Cash=cash.Cash-debit};
                        changed=position with{Pending=position.Pending-f.Quantity,Filled=position.Filled+f.Quantity,
                            Inventory=checked(position.Inventory+f.Quantity-(inBase?f.Fee:0)),AllocatedCost=checked(position.AllocatedCost+debit),Stage=NavalReservationStage.Confirmed};
                    }
                    candidate=candidate with{Accounts=candidate.Accounts.Select(a=>a.Account==cash.Account?cash:a).ToArray(),
                        Reservations=candidate.Reservations.Select(p=>p.Id==changed.Id?changed:p).ToArray(),Fills=candidate.Fills.Append(new NavalAcceptedFill(fillKey,digest,f)).ToArray()};
                    if(!f.Reduction&&f.Price>position.Intent.EntryPriceCeiling)candidate=candidate with{EmergencyLatch=true,Mode="STOP",Reason="EXECUTION_OVERRUN",Conflicts=candidate.Conflicts.Append(NavalJson.Serialize(f)).ToArray()};
                }
                foreach(string id in r.TerminalIds)
                {
                    var position=candidate.Reservations.SingleOrDefault(p=>p.Id==id);
                    if(!r.Final||!r.Complete||position==null||(expected==NavalEvidenceKind.Reject&&position.Filled!=0))return StopCandidate(e,"TERMINAL_CONFLICT",true,Conflict(r));
                    candidate=candidate with{Reservations=candidate.Reservations.Select(p=>p.Id==id?p with{Pending=0,TerminalCoverage=true,ReductionUnsettled=false,
                        Stage=expected==NavalEvidenceKind.Reject?NavalReservationStage.Rejected:NavalReservationStage.Terminal}:p).ToArray()};
                }
                if(r.Marks.Count!=_definition.Catalog.Count||!_definition.Catalog.All(i=>r.Marks.TryGetValue(i.Instrument,out var price)&&price>0))
                    return StopCandidate(e,"INCOHERENT_MARKS",true,Conflict(r));
                candidate=candidate with{Marks=new Dictionary<string,decimal>(r.Marks)};
                if(r.Complete)
                {
                    if(r.Cash.Count!=candidate.Accounts.Count||r.Cash.Distinct().Count()!=r.Cash.Count||!candidate.Accounts.All(a=>r.Cash.Contains(a)))return StopCandidate(e,"INCOHERENT_CASH_MARKS",true,Conflict(r));
                    var required=s.Reservations.Where(NavalCashRiskPolicy.Active).Select(p=>p.Id).Union(candidate.Reservations.Where(NavalCashRiskPolicy.Active).Select(p=>p.Id)).ToArray();
                    if(r.Observations.Count!=required.Length||r.Observations.Select(o=>o.ReservationId).Distinct().Count()!=required.Length||
                        !required.All(id=>r.Observations.Any(o=>{var p=candidate.Reservations.Single(x=>x.Id==id);return o.ReservationId==id&&o.Inventory==p.Inventory&&o.Pending==p.Pending;})))return StopCandidate(e,"INCOHERENT_ORDER_HOLDINGS",true,Conflict(r));
                }
                // Whole batch is coherent before marking, peak/day checks or publication.
                candidate=candidate with{Generation=r.Generation,Sequence=r.Sequence,Receipts=candidate.Receipts.Append(new NavalAcceptedReceipt(key,r.Digest)).ToArray()};
                if(expected==NavalEvidenceKind.Reconcile)
                    candidate=candidate with{ReconciledUtc=r.CaptureUtc,Mode="STOP",Reason="RECONCILED_EXPLICIT_START_REQUIRED",
                        Reservations=candidate.Reservations.Select(p=>p.Stage==NavalReservationStage.Uncertain?p with{Stage=p.Filled>0?NavalReservationStage.Confirmed:NavalReservationStage.Reserved}:p).ToArray()};
                candidate=NavalCashRiskPolicy.Refresh(candidate,_definition,r.CaptureUtc,expected==NavalEvidenceKind.Reconcile);
                var result=Commit(e,candidate,NavalMutationStatus.Applied,"EVIDENCE_APPLIED");
                if(result.Status==NavalMutationStatus.Applied&&expected==NavalEvidenceKind.Reconcile){_ready=true;_started=false;}
                return result;
            }
            catch(OverflowException){return StopCandidate(e,"ECONOMIC_OVERFLOW",true,Conflict(r));}
        }
    }
    internal NavalRiskResult MarkUncertain(string reservationId,string reasonCode)
    {
        lock(_owner){var e=_owner.Read();var s=e.Naval!;if(!s.Reservations.Any(p=>p.Id==reservationId&&NavalCashRiskPolicy.Active(p)))return Result(s,NavalMutationStatus.Refused,"UNKNOWN_RESERVATION");
            e.Naval=s with{Reservations=s.Reservations.Select(p=>p.Id==reservationId?p with{Stage=NavalReservationStage.Uncertain}:p).ToArray()};return StopCandidate(e,"UNCERTAIN:"+reasonCode);}
    }
    internal NavalRiskResult Stop(string reasonCode){lock(_owner){return StopCandidate(_owner.Read(),reasonCode);}}
    internal NavalRiskResult Start(NavalStartPermit permit)
    {
        lock(_owner){var e=_owner.Read();var s=e.Naval!;
            if(_frozen)return Result(s,NavalMutationStatus.Stopped,"STORAGE_FROZEN");
            if(permit==null||permit.OwnerToken!=OwnerToken||permit.Revision!=s.Revision||permit.Generation!=s.Generation||!permit.ExplicitOfflineIntent||!Fresh(s)||
                s.DailyLatch||s.DrawdownLatch||s.EmergencyLatch||s.Reservations.Any(p=>p.Stage==NavalReservationStage.Uncertain)||s.LiquidationEquity<=0||
                NavalCashRiskPolicy.Gross(s)>s.LiquidationEquity*_definition.Limits.MaxLeverage||NavalCashRiskPolicy.AccountNotionalBreach(s,_definition)||s.Accounts.Any(a=>a.Cash-s.Reservations.Where(p=>p.Account==a.Account).Sum(NavalCashRiskPolicy.Held)<0))return Result(s,NavalMutationStatus.Refused,"START_NOT_PERMITTED");
            var result=Commit(e,s with{Mode="RUNNING",Reason="EXPLICIT_OFFLINE_START"},NavalMutationStatus.Applied,"STARTED");if(result.Status==NavalMutationStatus.Applied)_started=true;return result;}
    }
}
