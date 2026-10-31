using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Services;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Tests;

internal sealed class OkxExecutionFixture : IDisposable
{
    internal readonly string DirectoryPath;
    internal string Target=>Path.Combine(DirectoryPath,"naval_authority_v1.json");
    internal readonly NavalTestClock Clock=new();
    internal readonly NavalAccountId Account=new("SYNTHETIC",NavalEnvironment.Offline,"account-0","USD");
    internal readonly OkxExecutionScope Scope=new("Demo","123456","123456","USD","fixture-key");
    internal readonly NavalPortfolioDefinition Definition;
    internal NavalOfflineExecutionBinding Binding=>new(Account,"Demo","123456","123456","USD","fixture-key","fixture-scope",ConfigDigest,"okx-cash-v1");
    private static readonly string ConfigDigest=Apex.Desktop.Okx.OkxConfiguration.Bind(new Apex.Desktop.Okx.OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new Apex.Desktop.Okx.OkxScopeOptions{
        ScopeId="fixture-scope",Environment="OkxDemo",Region="EEA",ExpectedUid="123456",ExpectedMainUid="123456",QuoteCurrency="USD",CredentialBindingId="fixture-key",SafetyVersion="okx-cash-v1"}}}).Single().ConfigDigest;
    internal NavalCoreStateOwner Owner=null!;
    internal NavalPortfolioCoordinator Coordinator=null!;
    internal IKeyValueStore Store=null!;
    internal NavalExecutionTransactions Transactions=null!;
    internal long Generation=1;
    internal OkxExecutionFixture(string? directory=null)
    {
        DirectoryPath=directory??Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")??Path.GetTempPath(),"okx-p-"+Guid.NewGuid().ToString("N"));
        Definition=new(new("p","USD"),new(),new[]{new NavalSeed(Account,10000m)},
            new[]{new NavalInstrument("BTC-USD","BTC","USD",.001m,.001m,.01m)},new(),
            Enumerable.Range(0,50).Select(n=>new NavalSignal("s-"+n,Account,NavalFleet.Alpha,"BTC-USD","fixture-rule",Clock.Now.AddDays(-1),Clock.Now.AddDays(1))));
        Open();
    }
    internal void Open(IKeyValueStore? store=null)
    {Store=store??new FileStore(DirectoryPath);Owner=new(DirectoryPath,Store,Definition);Coordinator=new(Owner,Definition,Clock);}
    internal void Restart(){Owner.Dispose();Generation++;Open();}
    internal void Run()
    {
        var s=Owner.Read().Naval!;
        var receipt=new NavalEvidenceReceipt(Coordinator.OwnerToken,Definition.Id,"reconcile-"+Generation,Generation,1,Clock.Now,Clock.Now,
            NavalEvidenceKind.Reconcile,true,false,new Dictionary<string,decimal>{{"BTC-USD",100m}},null,s.Accounts,
            s.Reservations.Where(NavalCashRiskPolicy.Active).Select(r=>new NavalObservation(r.Id,r.Inventory,r.Pending)),null);
        Assert.Equal(NavalMutationStatus.Applied,Coordinator.Reconcile(receipt).Status);
        Assert.Equal(NavalMutationStatus.Applied,Coordinator.Start(new(Coordinator.OwnerToken,Coordinator.Snapshot().DurableRevision,Generation,true)).Status);
    }
    internal NavalEntryIntent FinancialIntent(string command="financial-0",int signal=0)=>new(command,"s-"+signal,"BTC-USD",4m,100m,90m,130m);
    internal NavalRiskResult Reserve()=>Coordinator.ForFleet(Account,NavalFleet.Alpha).TryReserve(FinancialIntent());
    internal void Init()=>Transactions=NavalExecutionTransactions.ForOfflineTests(Coordinator,Binding);
    internal NavalExecutionIntent Intent(NavalRiskResult reservation,OkxPreparedRequest wire,string id="execution-0",params string[] allocationKeys)
    {
        var e=Owner.Read();var s=e.Naval!;var r=s.Reservations.Single(x=>x.Id==reservation.ReservationId);
        return new(){IntentId=id,ReservationId=r.Id,CommandId=r.Intent.CommandId,SignalId=r.Intent.SignalId,
            Operation=Enum.Parse<NavalExecutionOperation>(wire.Operation.ToString()),Binding=Binding,Method=wire.Method,Path=wire.Path,
            Instrument=wire.Instrument,OriginalClientId=wire.OriginalClientId,OrderId=wire.OrderId,AllocationKeys=allocationKeys.Length==0?new[]{id}:allocationKeys,
            Body=wire.CopyBody(),RequestDigest=wire.Digest,Deadline=wire.Deadline,PreparedRevision=e.DurableRevision+1,
            ReservationDigest=NavalJson.Hash(r),DefinitionDigest=Definition.Digest,CatalogDigest=NavalJson.Hash(Definition.Catalog),
            SourceGeneration=s.Generation,SourceSequence=s.Sequence,Stage=NavalExecutionStage.Prepared};
    }
    internal void Holds()
    {
        var v=Coordinator.Snapshot();var r=Assert.Single(v.Reservations);
        Assert.Equal(400.400m,r.EntryHold);Assert.Equal(.520m,r.CloseFeeHold);Assert.Equal(40.760m,r.RiskHold);
        Assert.Equal(400.920m,v.Held);Assert.Equal(9599.080m,v.Free);Assert.Equal(10000m,v.Cash);
        Assert.Equal(0m,r.Inventory);Assert.Equal(4m,r.Pending);Assert.False(r.TerminalCoverage);Assert.False(r.ReductionUnsettled);
        Assert.Empty(Owner.Read().Naval!.Fills);
        string publication=NavalJson.Serialize(Coordinator.ReadPublication());
        Assert.DoesNotContain("fixture-key",publication);Assert.DoesNotContain("CredentialBindingId",publication);
        Assert.DoesNotContain("PreparationSessionId",publication);Assert.DoesNotContain("OriginalClientId",publication);
    }
    public void Dispose(){Owner?.Dispose();if(Directory.Exists(DirectoryPath))Directory.Delete(DirectoryPath,true);}
}
