using System;
using System.Collections.Generic;
using System.Linq;
namespace Apex.Core.Naval;

public sealed record NavalFill(NavalAccountId Account, string Instrument, string TradeId, string ReservationId,
    bool Reduction, decimal Quantity, decimal Price, decimal Fee, string FeeCurrency, DateTimeOffset TradeUtc);
internal enum NavalEvidenceKind { Reconcile, Confirm, Reject, Terminal, Acknowledgement }
internal sealed record NavalObservation(string ReservationId, decimal Inventory, decimal Pending);
internal sealed class NavalEvidenceReceipt
{
    internal Guid OwnerToken { get; }
    internal NavalPortfolioId Portfolio { get; }
    internal string ReceiptId { get; }
    internal long Generation { get; }
    internal long Sequence { get; }
    internal DateTimeOffset CaptureUtc { get; }
    internal DateTimeOffset ReceiveUtc { get; }
    internal NavalEvidenceKind Kind { get; }
    internal bool Complete { get; }
    internal bool Final { get; }
    internal IReadOnlyDictionary<string, decimal> Marks { get; }
    internal IReadOnlyList<NavalFill> Fills { get; }
    internal IReadOnlyList<NavalCashAccount> Cash { get; }
    internal IReadOnlyList<NavalObservation> Observations { get; }
    internal IReadOnlyList<string> TerminalIds { get; }
    internal string Digest => NavalJson.Hash(new { Portfolio, ReceiptId, Generation, Sequence, CaptureUtc,
        ReceiveUtc, Kind, Complete, Final, Marks, Fills, Cash, Observations, TerminalIds });
    // Only trusted in-process friends can issue offline simulation evidence. No transport deserialization constructor.
    internal NavalEvidenceReceipt(Guid ownerToken, NavalPortfolioId portfolio, string receiptId, long generation,
        long sequence, DateTimeOffset captureUtc, DateTimeOffset receiveUtc, NavalEvidenceKind kind,
        bool complete, bool final, IReadOnlyDictionary<string, decimal> marks, IEnumerable<NavalFill>? fills = null,
        IEnumerable<NavalCashAccount>? cash = null, IEnumerable<NavalObservation>? observations = null,
        IEnumerable<string>? terminalIds = null)
    {
        OwnerToken = ownerToken; Portfolio = portfolio; ReceiptId = receiptId; Generation = generation;
        Sequence = sequence; CaptureUtc = captureUtc; ReceiveUtc = receiveUtc; Kind = kind;
        Complete = complete; Final = final;
        Marks = new System.Collections.ObjectModel.ReadOnlyDictionary<string, decimal>(new Dictionary<string, decimal>(marks));
        Fills = Array.AsReadOnly((fills ?? Array.Empty<NavalFill>()).ToArray());
        Cash = Array.AsReadOnly((cash ?? Array.Empty<NavalCashAccount>()).ToArray());
        Observations = Array.AsReadOnly((observations ?? Array.Empty<NavalObservation>()).ToArray());
        TerminalIds = Array.AsReadOnly((terminalIds ?? Array.Empty<string>()).ToArray());
    }
}
internal sealed class NavalStartPermit
{
    internal Guid OwnerToken { get; }
    internal long Revision { get; }
    internal long Generation { get; }
    internal bool ExplicitOfflineIntent { get; }
    internal NavalStartPermit(Guid ownerToken, long revision, long generation, bool explicitOfflineIntent)
    { OwnerToken = ownerToken; Revision = revision; Generation = generation; ExplicitOfflineIntent = explicitOfflineIntent; }
}
internal interface INavalClock { DateTimeOffset UtcNow { get; } }
