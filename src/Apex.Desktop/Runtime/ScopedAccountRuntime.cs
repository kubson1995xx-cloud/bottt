using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Apex.Core.Bridge;
using Apex.Core.Engine;
using Apex.Desktop.Bridge;

namespace Apex.Desktop.Runtime;

public sealed partial class AccountRuntime
{
    private readonly string[] _priorScopeInventory;
    private readonly bool _priorAccountConfiguration;
    private ScopedReceiptAuthority? _authority;
    private long _observationSequence;
    private string _scopeSetupMessage = "Utwórz zakres tylko dla nowej, dedykowanej instalacji DEMO hedged; stare dane wymagają uzgodnienia, nie rotacji etykiety.";
    internal string ResolvedLabel => Settings.ScopeReservation?.Identity.Label ?? _config.Config.Label;
    private void ConfigureOwner()
    {
        if (_engine == null) return;
        _observationSequence = Math.Max(_observationSequence, _engine.LastScopedObservationSequence);
        var engine = _engine;
        _authority ??= new ScopedReceiptAuthority(engine, receipt =>
        {
            var s = _adapter.Session;
            return !_stop && _sessionReady && !_storageFault && !_emergencyLatched && s != null
                && MatchesIdentity(s.Account!) && s.IsCurrent(receipt);
        }, BridgeSession.MonotonicNow);
    }
    private void UpdateRecoveryOperations()
    {
        if (_engine == null || _adapter.Session == null) return;
        _adapter.Session.RecoverOperations = _engine.ScopedAdmissions.Where(a => a.Stage == AdmissionStage.OutcomeUnknown).Select(a => a.OperationId).ToArray();
    }
    internal void OnScopedHistory(BridgeSession s, ScopedSnapshot snapshot)
    {
        lock (_lifecycle)
        {
            if (_stop || !ReferenceEquals(_adapter.Session, s)) return;
            // Authorize the immutable completed receipt NOW. This closure may account it after
            // attachment replacement, but the value-based live fence cannot grant a new permit.
            Enqueue(() =>
            {
                ConfigureOwner();
                if (_engine == null || _authority == null) return;
                foreach (var result in snapshot.Recovered)
                    if (_engine.ScopedAdmissions.Any(a => a.OperationId == result.Id && a.Stage == AdmissionStage.OutcomeUnknown))
                        _engine.RecoverOpeningOutcome(result.Id, ProtocolResult.ToOutcome(result), DateTime.UtcNow);
                var receipt = snapshot.Receipt with { Sequence = checked(++_observationSequence) };
                if (_engine.ScopedBaseline?.Stage == BaselineStage.Proposed) _engine.EstablishBaseline(receipt, _authority, DateTime.UtcNow);
                else _engine.ReconcileScoped(receipt, _authority, DateTime.UtcNow);
                UpdateRecoveryOperations();
            });
        }
    }
    internal void OnBrokerChanged(BridgeSession s)
    {
        if (!ReferenceEquals(_adapter.Session, s)) return;
        Enqueue(() => { if (ReferenceEquals(_adapter.Session, s)) { _engine?.CmdStop(); _engine?.SaveState(DateTime.UtcNow, true); } });
    }
    private string SetupScope(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("confirmDedicatedDemo", out var confirmed) || confirmed.ValueKind != JsonValueKind.True)
            return "Wymagane potwierdzenie dedykowanego DEMO hedged i wyłącznego pisarza (confirmDedicatedDemo). To tylko konfiguracja read-only, bez transakcji.";
        var s = _adapter.Session; var e = _engine;
        if (Settings.ScopeReservation != null || e?.ScopedBaseline != null) return "Zakres już zarezerwowany. Skopiuj dokładną etykietę do cBota i połącz ponownie; bez tworzenia kolejnej epoki.";
        if (_priorScopeInventory.Length > 0 || _priorAccountConfiguration)
            return _scopeSetupMessage = "Konflikt: istnieją stare dane/config konta/dzienniki/stany. Brak stanu obok starych plików NIE oznacza świeżej instalacji. Uzgodnij i zachowaj dowody; nie zmieniaj etykiety.";
        if (e == null || s == null || !s.ScopedCapable || !s.HasFreshSnapshots || !s.BrokerConnected || s.Account is not { IsLive: false, Hedging: true }
            || s.DeploymentInventory == null || s.DeploymentInventory.Count == 0 || s.DeploymentInventory.Any(x => !x.Healthy || x.Missing || x.Entries != 0)
            || _storageFault || _store.HasFault || !_journal.IsHealthy || _emergencyLatched || !EvaluateGuard().Allowed
            || s.Positions.Count != 0 || s.PendingOrders.Count != 0)
            return _scopeSetupMessage = "Konfiguracja zablokowana: wymagane świeże DEMO hedged, scoped-recovery-v1, zdrowy zapis oraz ZERO wszystkich pozycji i zleceń oczekujących. Obcych obiektów nie zamykamy.";
        // Inventory EVERY deployment file before changing a label. Only this process's freshly
        // initialized empty account is eligible; pre-existing files were captured before writes.
        var currentFiles = Directory.GetFiles(Path.Combine(_config.DataDir, "konta"), "*", SearchOption.AllDirectories);
        if (currentFiles.Any(p => !p.StartsWith(_dir + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            return _scopeSetupMessage = "Konflikt: wykryto dodatkowy/stary zakres instalacji – wymagane uzgodnienie.";
        var a = s.Account;
        var proposal = TradingEngine.CreateBaselineProposal(new RecoveryIdentity { Platform = Platform, Broker = a!.Broker, AccountNumber = a.Number,
            Currency = a.Currency, IsLive = a.IsLive, IsHedging = a.Hedging, ExecutionPath = RecoveryExecutionPath.DesktopBridge, SafetyVersion = ScopedRecoveryPolicy.SafetyVersion });
        string previousLabel = e.Config.Label;
        e.CmdStop(); e.Config.Label = proposal.Identity.Label;
        if (!e.ReserveBaseline(proposal, DateTime.UtcNow))
        { e.Config.Label = previousLabel; return _scopeSetupMessage = "Rezerwacja zakresu odrzucona: stare zobowiązania/latch/błąd zapisu. Niczego nie usuwaj."; }
        try
        {
            _config.Update(c => { c.Accounts[Key].ScopeReservation = proposal; c.Accounts[Key].ScopeInventory = currentFiles.Select(p => Path.GetRelativePath(_config.DataDir, p)).ToArray(); });
        }
        catch { _storageFault = true; throw; } // ambiguous config/state pair is never rotated/retried
        s.ConfigureScope(proposal.BaselineId);
        _ = s.CloseAsync("read-only konfiguracja: ustaw nową etykietę cBota i połącz ponownie");
        return _scopeSetupMessage = $"Skopiuj dokładną etykietę {proposal.Identity.Label} do parametru cBota „Dokładna etykieta pozycji APEX”, zatrzymaj/uruchom most i połącz ponownie. Zakres {proposal.BaselineId}; bez automatycznej transakcji. START strategii czeka na Task2 PASS.";
    }
    private object ScopeStatus()
    {
        var b = _engine?.ScopedBaseline ?? Settings.ScopeReservation;
        bool eligible = _engine != null && _authority != null && _engine.GetScopedPermit(_authority, DateTime.UtcNow) != null;
        return new { baselineId = b?.BaselineId ?? Guid.Empty, label = b?.Identity.Label ?? ResolvedLabel,
            startUtc = b?.Stage == BaselineStage.Established ? b.EstablishedUtc : (DateTime?)null,
            status = eligible ? "ScopedReady" : _engine?.ScopedStatus.ToString() ?? "Unestablished", brokerCoverageUnknown = true,
            eligibleTechnicalTest = eligible, strategyStartBlocked = true, pendingDeficits = _engine?.ScopedAdmissions.Count(a => a.Stage == AdmissionStage.OutcomeUnknown) ?? 0,
            message = _engine?.ScopedBaseline != null ? _engine.ScopedMessage : _scopeSetupMessage,
            brokerEpoch = _adapter.Session?.BrokerEpoch, brokerConnected = _adapter.Session?.BrokerConnected == true,
            nextAction = eligible ? "Osobno zatwierdź test techniczny DEMO. START wymaga Task2 PASS; test nie uruchamia strategii." : _scopeSetupMessage,
            policy = "Dedykowane konto DEMO hedged, wyłączny pisarz; brak cudzych pozycji/zleceń. Broker CoverageUnknown. Statystyki dotyczą tej epoki, ryzyko całego obserwowanego konta." };
    }
}
