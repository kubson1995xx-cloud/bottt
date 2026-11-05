using System.Linq;
using Apex.Core.Naval;
using Xunit;

namespace Apex.Tests;

public class NavalCashFinalityRegressionTests
{
    // An entry's final proof must not settle a later nonfinal reduction, or revive cancelled entry quantity.
    [Theory]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 4)] [InlineData(true, 4)]
    public void NavalCash_EntryTerminalBeforeReduction_RetainsOwnershipUntilFinalCoverage(bool restart, int bought)
    {
        using var f = new NavalCashFixture();
        f.Run();
        var r = f.Reserve();
        Assert.Equal(NavalMutationStatus.Applied, r.Status);
        var buy = f.Fill(r, bought, 100, bought == 2 ? .2m : .4m);
        Assert.Equal(NavalMutationStatus.Applied, f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm, fills: new[] { buy })).Status);
        Assert.Equal(NavalMutationStatus.Applied, f.C.Terminal(f.Receipt(NavalEvidenceKind.Terminal,
            complete: true, final: true, terminal: new[] { r.ReservationId! })).Status);
        Assert.Equal(0, Assert.Single(f.View.Reservations).Pending);
        Assert.Equal(bought == 2 ? .260m : .520m, f.View.Held);

        var sell = f.Fill(r, bought, 110, bought == 2 ? .22m : .44m, true, "sell-after-entry-final");
        Assert.Equal(NavalMutationStatus.Applied, f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm, fills: new[] { sell })).Status);
        decimal expectedCash = bought == 2 ? 10019.580m : 10039.160m;
        Assert.Equal(expectedCash, f.View.Cash);
        Assert.Equal(0, Assert.Single(f.View.Reservations).Inventory);
        Assert.Equal(0, Assert.Single(f.View.Reservations).Pending);
        Assert.Equal(1, f.View.ActiveCount);
        Assert.Equal(bought == 2 ? .260m : .520m, f.View.Held);
        Assert.True(Assert.Single(f.View.Reservations).TerminalCoverage); // Entry is still final.

        if (restart)
        {
            f.Restart();
            Assert.Equal("STOP", f.View.Mode);
            Assert.False(f.View.Ready);
            Assert.Equal(expectedCash, f.View.Cash);
            Assert.Equal(1, f.View.ActiveCount);
            Assert.Equal(0, Assert.Single(f.View.Reservations).Pending);
            f.Run(); // Fresh reconciliation and explicit START do not imply reduction finality.
        }
        Assert.Equal("SYMBOL_OWNED", f.Reserve(fleet: NavalFleet.Bravo).Code);
        Assert.Equal(1, f.View.ActiveCount);
        Assert.Equal(expectedCash, f.View.Cash);
        Assert.Equal(0, Assert.Single(f.View.Reservations).Pending);

        // A complete but nonfinal reconciliation and an exact economic replay cannot release the slot/fee buffer.
        Assert.Equal(NavalMutationStatus.Applied, f.Reconcile().Status);
        Assert.Equal(NavalMutationStatus.Applied, f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm, fills: new[] { sell })).Status);
        Assert.Equal(expectedCash, f.View.Cash);
        Assert.Equal(1, f.View.ActiveCount);
        Assert.Equal(bought == 2 ? .260m : .520m, f.View.Held);
        var final = f.Receipt(NavalEvidenceKind.Terminal, complete: true, final: true, terminal: new[] { r.ReservationId! });
        Assert.Equal(NavalMutationStatus.Applied, f.C.Terminal(final).Status);
        Assert.Equal(0, f.View.ActiveCount);
        Assert.Equal(0, f.View.Held);
        Assert.Equal(expectedCash, f.View.Cash);
        Assert.Equal(NavalMutationStatus.Duplicate, f.C.Terminal(final).Status);
        f.Restart();
        f.Run();
        Assert.Equal(0, f.View.ActiveCount);
        Assert.Equal(expectedCash, f.View.Cash);
        Assert.Equal(2, f.Owner.Read().Naval!.Fills.Count);
        Assert.Equal(NavalMutationStatus.Applied, f.Reserve(fleet: NavalFleet.Bravo, signal: 1).Status);
    }
}
