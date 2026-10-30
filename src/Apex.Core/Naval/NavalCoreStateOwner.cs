using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Apex.Core.Engine;
namespace Apex.Core.Naval;
internal interface INavalStateOwner
{
    EngineState Read();
    NavalCommitResult TryCommit(long expectedRevision, EngineState candidate);
}
internal enum NavalCommitResult { Committed, Conflict, Faulted }

// A dedicated directory is the exclusivity domain, not a fleet or broker account key.
internal sealed class NavalCoreStateOwner : INavalStateOwner, IDisposable
{
    internal const string StateKey = "naval-authority-v1";
    private readonly IKeyValueStore _store;
    private readonly NavalPortfolioDefinition _definition;
    private readonly FileStream _lease;
    private EngineState _state=new();
    private bool _faulted, _disposed;
    internal NavalCoreStateOwner(string directory,IKeyValueStore store,NavalPortfolioDefinition definition)
    {
        NavalCashRiskPolicy.ValidateDefinition(definition);_store=store;_definition=definition;
        directory=Path.GetFullPath(directory);Directory.CreateDirectory(directory);
        // FileShare.None plus the OS byte lock protects cooperating processes and separate coordinators.
        _lease=new FileStream(Path.Combine(directory,".naval-owner.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        try
        {
            if(OperatingSystem.IsMacOS())throw new PlatformNotSupportedException("Naval directory lease requires Linux or Windows");
            _lease.Lock(0,1);
            string marker=Path.Combine(directory,".naval-initialized");var raw=store.Get(StateKey);
            if(raw==null)
            {
                if(File.Exists(marker))throw new InvalidDataException("Missing prior Naval authority; never reseed");
                using(var m=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.None)){m.WriteByte(1);m.Flush(true);}
                var initial=new NavalPortfolioState{Schema="naval-cash-risk-v1",Id=definition.Id,ConfigDigest=definition.Digest,
                    Accounts=definition.Members.Select(m=>new NavalCashAccount(m.Account,m.Allocation,m.Allocation)).ToArray()};
                _state=new EngineState{Naval=Seal(initial)};
                Validate(_state);store.Set(StateKey,NavalJson.Serialize(_state));store.Flush();
            }
            else
            {
                if(!File.Exists(marker))throw new InvalidDataException("Authority without initialization manifest");
                using var doc=JsonDocument.Parse(raw);RequireProperties(doc.RootElement.GetProperty("Naval"),typeof(NavalPortfolioState));
                foreach(var r in doc.RootElement.GetProperty("Naval").GetProperty("Reservations").EnumerateArray())RequireProperties(r,typeof(NavalReservation));
                if(doc.RootElement.EnumerateObject().Count(p=>p.Name=="NavalReadJournal")>1)throw new InvalidDataException("Duplicate publication metadata");
                if(doc.RootElement.TryGetProperty("NavalReadJournal",out var metadata)&&metadata.ValueKind!=JsonValueKind.Null)NavalReadJournal.RequireMetadata(metadata);
                _state=JsonSerializer.Deserialize<EngineState>(raw,NavalJson.Options)??throw new InvalidDataException("Null authority");Validate(_state);
            }
        }
        catch{_lease.Dispose();throw;}
    }
    private static void RequireProperties(JsonElement e,Type t)
    {foreach(var p in t.GetProperties())if(!e.TryGetProperty(p.Name,out _))throw new InvalidDataException("Missing required "+p.Name);}
    public EngineState Read(){lock(this){if(_disposed)throw new ObjectDisposedException(nameof(NavalCoreStateOwner));return NavalJson.Clone(_state);}}
    public NavalCommitResult TryCommit(long expectedRevision,EngineState candidate)
    {
        lock(this)
        {
            if(_faulted||_disposed)return NavalCommitResult.Faulted;
            if(expectedRevision!=_state.DurableRevision)return NavalCommitResult.Conflict;
            try
            {
                candidate=NavalJson.Clone(candidate);
                if(candidate.DurableRevision!=checked(expectedRevision+1))throw new InvalidDataException("Revision discontinuity");
                candidate.Naval=Seal(candidate.Naval??throw new InvalidDataException("Missing Naval state"));
                NavalReadJournal.ValidateTransition(_state,candidate);
                Validate(candidate);_store.Set(StateKey,NavalJson.Serialize(candidate));_store.Flush();_state=candidate;return NavalCommitResult.Committed;
            }
            catch{_faulted=true;return NavalCommitResult.Faulted;}
        }
    }
    private static NavalPortfolioState Seal(NavalPortfolioState s)=>s with{StateDigest=NavalJson.Hash(s with{StateDigest=""})};
    private void Validate(EngineState envelope)
    {
        NavalCashRiskPolicy.ValidateDefinition(_definition);
        var s=envelope.Naval??throw new InvalidDataException("Unconfigured authority");
        if(s.StateDigest!=Seal(s).StateDigest)throw new InvalidDataException("Naval state integrity digest mismatch");
        var c=NavalJson.Clone(envelope);c.Naval=null;c.NavalReadJournal=null;c.DurableRevision=0;c.SavedUtc=default;
        if(NavalJson.Serialize(c)!=NavalJson.Serialize(new EngineState()))throw new InvalidDataException("CFD-populated envelope is not a Naval scope");
        if(s.Schema!="naval-cash-risk-v1"||s.Id!=_definition.Id||s.ConfigDigest!=_definition.Digest||s.Revision!=envelope.DurableRevision||s.Generation<0||s.Sequence<0||
            (s.Mode!="STOP"&&s.Mode!="RUNNING"))throw new InvalidDataException("Naval schema/config/scope/revision mismatch");
        if(s.Accounts.Count!=_definition.Members.Count||s.Accounts.Select(a=>a.Account).Distinct().Count()!=s.Accounts.Count)throw new InvalidDataException("Membership changed");
        foreach(var a in s.Accounts)
        {
            var seed=_definition.Members.SingleOrDefault(m=>m.Account==a.Account)??throw new InvalidDataException("Unknown account");
            decimal cash=seed.Allocation;
            foreach(var f in s.Fills.Where(f=>f.Fill.Account==a.Account).Select(f=>f.Fill))
                cash=checked(cash+(f.Reduction?f.Quantity*f.Price:-f.Quantity*f.Price)-(f.FeeCurrency==a.Account.QuoteCurrency?f.Fee:0));
            if(a.Seed!=seed.Allocation||a.Cash!=cash||a.Cash<0)throw new InvalidDataException("Cash postings disagree");
        }
        if(s.Reservations.Select(r=>r.Id).Distinct().Count()!=s.Reservations.Count||s.Commands.Select(c=>c.CommandId).Distinct().Count()!=s.Commands.Count||
            s.Fills.Select(f=>f.Key).Distinct().Count()!=s.Fills.Count||s.Receipts.Select(e=>e.Key).Distinct().Count()!=s.Receipts.Count)throw new InvalidDataException("Duplicate durable identity");
        foreach(var r in s.Reservations)
        {
            if(!NavalCashRiskPolicy.Text(r.Id)||r.Intent==null||!Enum.IsDefined(typeof(NavalFleet),r.Fleet)||!Enum.IsDefined(typeof(NavalReservationStage),r.Stage)||
                !s.Accounts.Any(a=>a.Account==r.Account)||!_definition.Catalog.Any(i=>i.Instrument==r.Intent.Instrument)||
                r.Quantity<=0||r.Pending<0||r.Filled<0||r.Filled>r.Quantity||r.Pending+r.Filled>r.Quantity||r.Inventory<0||r.AllocatedCost<0||
                !s.Commands.Any(c=>c.CommandId==r.Intent.CommandId&&c.SignalId==r.Intent.SignalId&&c.Result.ReservationId==r.Id))throw new InvalidDataException("Incomplete reservation");
            var h=NavalCashRiskPolicy.Holds(r,_definition,s.Marks);
            if(h.EntryHold!=r.EntryHold||h.CloseFeeHold!=r.CloseFeeHold||h.RiskHold!=r.RiskHold)throw new InvalidDataException("Reservation holds disagree");
            decimal inventory=0,filled=0,cost=0;
            var instrument=_definition.Catalog.Single(i=>i.Instrument==r.Intent.Instrument);
            foreach(var item in s.Fills.Where(f=>f.Fill.ReservationId==r.Id))
            {
                var f=item.Fill;bool baseFee=f.FeeCurrency==instrument.BaseCurrency;
                if(f.Reduction){decimal removed=f.Quantity+(baseFee?f.Fee:0);if(removed>inventory)throw new InvalidDataException("Negative inventory");cost=inventory==0?0:cost*(inventory-removed)/inventory;inventory-=removed;}
                else {filled+=f.Quantity;inventory+=f.Quantity-(baseFee?f.Fee:0);cost+=f.Quantity*f.Price+(baseFee?0:f.Fee);}
            }
            if(inventory!=r.Inventory||filled!=r.Filled||cost!=r.AllocatedCost)throw new InvalidDataException("Inventory/cost postings disagree");
        }
        if(s.Reservations.Where(NavalCashRiskPolicy.Active).GroupBy(r=>(r.Account,r.Intent.Instrument)).Any(g=>g.Count()>1))throw new InvalidDataException("Duplicate symbol owners");
        foreach(var f in s.Fills)
            if(f.Digest!=NavalJson.Hash(f.Fill)||f.Key!=FillKey(f.Fill)||!s.Reservations.Any(r=>r.Id==f.Fill.ReservationId&&r.Account==f.Fill.Account&&r.Intent.Instrument==f.Fill.Instrument))
                throw new InvalidDataException("Fill identity mismatch");
        NavalReadJournal.Validate(envelope,_definition);
    }
    internal static string FillKey(NavalFill f)=>NavalJson.Hash(new{f.Account.Venue,f.Account.Environment,f.Account.AccountId,f.Instrument,f.TradeId});
    public void Dispose(){lock(this){if(_disposed)return;_disposed=true;_lease.Dispose();}}
}
