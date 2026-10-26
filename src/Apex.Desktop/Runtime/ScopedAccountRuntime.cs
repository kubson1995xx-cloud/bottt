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
    private readonly bool _bootstrapProvenance;
    private readonly RecoveryIdentity? _bootstrapIdentity;
    private sealed record BootstrapClaim(string Version, RecoveryIdentity Identity, DateTime CreatedUtc);
    private System.Collections.Generic.HashSet<string> BootstrapFiles(RecoveryIdentity identity)
    {
        string key = $"APEX {new string(identity.Label.Where(char.IsLetterOrDigit).ToArray())} {identity.AccountNumber}";
        if (key.Length > 50) key = key.Substring(0, 50);
        return new(StringComparer.Ordinal) { Path.Combine(_dir, "scope-bootstrap.json"),
            Path.Combine(_dir, "stan", new string(key.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + ".json"),
            Path.Combine(_dir, "stan", "desktop_control.json"), Path.Combine(_dir, "dziennik", "transakcje.csv"),
            Path.Combine(_dir, "dziennik", "zdarzenia.log"), Path.Combine(_dir, "dziennik", "sygnaly.log"), Path.Combine(_dir, "kapital.json") };
    }
    private bool InitializeBootstrap(RecoveryIdentity? identity, bool directoryExisted)
    {
        if (identity?.Valid != true || identity.Platform != "cTrader") return false;
        string marker = Path.Combine(_dir, "scope-bootstrap.json");
        try
        {
            if (!directoryExisted && !_priorAccountConfiguration && _priorScopeInventory.Length == 0
                && Directory.GetDirectories(Path.Combine(_config.DataDir, "konta")).All(p => p == _dir))
            {
                Apex.Desktop.Services.AtomicFile.Write(marker, JsonSerializer.Serialize(new BootstrapClaim("fresh-runtime-v1", identity, DateTime.UtcNow), Apex.Desktop.Services.ConfigStore.Json));
                return true;
            }
            if (!File.Exists(marker) || _config.Config.Accounts.Keys.Any(k => k != Key)) return false;
            var claim = JsonSerializer.Deserialize<BootstrapClaim>(File.ReadAllText(marker), Apex.Desktop.Services.ConfigStore.Json);
            if (claim?.Version != "fresh-runtime-v1" || claim.Identity != identity || claim.CreatedUtc.Kind != DateTimeKind.Utc || claim.CreatedUtc > DateTime.UtcNow) return false;
            string stateKey = $"APEX {new string(identity.Label.Where(char.IsLetterOrDigit).ToArray())} {identity.AccountNumber}";
            if (stateKey.Length > 50) stateKey = stateKey.Substring(0, 50);
            string state = Path.Combine(_dir, "stan", new string(stateKey.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + ".json");
            // A retained marker is NOT permission to recreate missing authoritative state.
            if (!File.Exists(state)) return false;
            var allowed = BootstrapFiles(identity);
            return _priorScopeInventory.All(allowed.Contains);
        }
        catch { return false; } // original bytes remain; setup stays quarantined
    }
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
            var frozenReceipt = JsonSerializer.Deserialize<ScopedReceipt>(JsonSerializer.Serialize(snapshot.Receipt))!;
            var frozenResults = BridgeProtocol.Deserialize<ResultMsg[]>(BridgeProtocol.Serialize(snapshot.Recovered))!;
            Enqueue(() =>
            {
                ConfigureOwner();
                if (_engine == null || _authority == null) return;
                foreach (var result in frozenResults)
                    if (_engine.ScopedAdmissions.Any(a => a.OperationId == result.Id && a.Stage == AdmissionStage.OutcomeUnknown))
                        _engine.RecoverOpeningOutcome(result.Id, ProtocolResult.ToOutcome(result), DateTime.UtcNow);
                var receipt = frozenReceipt with { Sequence = checked(++_observationSequence) };
                if (_engine.ScopedBaseline?.Stage == BaselineStage.Proposed)
                {
                    if (s.DeploymentInventory?.All(x => x.Healthy && !x.Missing && x.Entries == 0) == true)
                        _engine.EstablishBaseline(receipt, _authority, DateTime.UtcNow);
                }
                else _engine.ReconcileScoped(receipt, _authority, DateTime.UtcNow);
                UpdateRecoveryOperations();
            });
        }
    }
    internal void OnBrokerChanged(BridgeSession s)
    {
        if (!ReferenceEquals(_adapter.Session, s)) return;
        Enqueue(() => { if (ReferenceEquals(_adapter.Session, s)) { _engine?.InvalidateScopedRuntime("zmiana epoki/łączności brokera"); _engine?.CmdStop(); _engine?.SaveState(DateTime.UtcNow, true); } });
    }
    private string SetupScope(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("confirmDedicatedDemo", out var confirmed) || confirmed.ValueKind != JsonValueKind.True)
            return "Wymagane potwierdzenie dedykowanego DEMO hedged i wyłącznego pisarza (confirmDedicatedDemo). To tylko konfiguracja read-only, bez transakcji.";
        var s = _adapter.Session; var e = _engine;
        if (Settings.ScopeReservation != null || e?.ScopedBaseline != null) return "Zakres już zarezerwowany. Skopiuj dokładną etykietę do cBota i połącz ponownie; bez tworzenia kolejnej epoki.";
        if (!_bootstrapProvenance)
            return _scopeSetupMessage = "Konflikt: istnieją stare dane/config konta/dzienniki/stany. Brak stanu obok starych plików NIE oznacza świeżej instalacji. Uzgodnij i zachowaj dowody; nie zmieniaj etykiety.";
        if (e == null || s == null || !s.ScopedCapable || !s.SourceInventoryReady || s.Account is not { IsLive: false, Hedging: true }
            || s.DeploymentInventory == null || s.DeploymentInventory.Count == 0 || s.DeploymentInventory.Any(x => !x.Healthy || x.Missing || x.Entries != 0)
            || _storageFault || _store.HasFault || !_journal.IsHealthy || _emergencyLatched || !EvaluateGuard().Allowed
            || s.Positions.Count != 0 || s.PendingOrders.Count != 0)
            return _scopeSetupMessage = "Konfiguracja zablokowana: wymagane świeże DEMO hedged, scoped-recovery-v1, zdrowy zapis oraz ZERO wszystkich pozycji i zleceń oczekujących. Obcych obiektów nie zamykamy.";
        // Inventory EVERY deployment file before changing a label. Only this process's freshly
        // initialized empty account is eligible; pre-existing files were captured before writes.
        var currentFiles = Directory.GetFiles(Path.Combine(_config.DataDir, "konta"), "*", SearchOption.AllDirectories);
        var allowedFiles = BootstrapFiles(_bootstrapIdentity!);
        if (currentFiles.Any(p => !allowedFiles.Contains(p)))
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
        var certificate = _engine?.GetExecutionReadiness(DateTime.UtcNow);
        return new { baselineId = b?.BaselineId ?? Guid.Empty, label = b?.Identity.Label ?? ResolvedLabel,
            startUtc = b?.Stage == BaselineStage.Established ? b.EstablishedUtc : (DateTime?)null,
            status = eligible ? "ScopedReady" : _engine?.ScopedStatus == ScopedRecoveryStatus.Ready ? "AwaitingEvidence" : _engine?.ScopedStatus.ToString() ?? "Unestablished", brokerCoverageUnknown = true,
            eligibleTechnicalTest = eligible && certificate?.Status != "InProgress", strategyStartBlocked = certificate?.CanStart != true,
            certificate, pendingDeficits = _engine?.ScopedDeficits ?? Array.Empty<string>(),
            uncertainOpenings = _engine?.ScopedAdmissions.Count(a => a.Stage == AdmissionStage.OutcomeUnknown) ?? 0,
            message = !eligible && _engine?.ScopedStatus == ScopedRecoveryStatus.Ready
                ? "AwaitingEvidence: receipt wygasła lub zmieniły się epoka/revizja/połączenie – wymagane świeże uzgodnienie"
                : _engine?.ScopedBaseline != null ? _engine.ScopedMessage : _scopeSetupMessage,
            brokerEpoch = _adapter.Session?.BrokerEpoch, brokerConnected = _adapter.Session?.BrokerConnected == true,
            nextAction = eligible ? certificate?.Message ?? "Osobno zatwierdź test techniczny DEMO. START wymaga trwałego PASS; test nie uruchamia strategii."
                : b == null ? _scopeSetupMessage : _adapter.Session?.ProtocolFault == true ? "Błąd protokołu: połącz most ponownie; bez kasowania stanu."
                : "Sprawdź dokładną etykietę/epokę, świeżą synchronizację i brakujące dowody. Nie usuwaj stanu ani rezerwacji. " + _engine?.ScopedMessage,
            policy = "Dedykowane konto DEMO hedged, wyłączny pisarz; brak cudzych pozycji/zleceń. Broker CoverageUnknown. Statystyki dotyczą tej epoki, ryzyko całego obserwowanego konta." };
    }
}
