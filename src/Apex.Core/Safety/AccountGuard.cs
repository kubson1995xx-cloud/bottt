using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Model;

namespace Apex.Core.Safety
{
    public sealed class AccountGuardSettings
    {
        /// <summary>Zezwolenie na konto LIVE. Domyślnie FALSE. Samo włączenie nie wystarcza – wymagane są dodatkowe warunki.</summary>
        public bool AllowLive { get; init; }

        /// <summary>Fraza potwierdzenia LIVE. Musi dokładnie odpowiadać <see cref="AccountGuard.RequiredLivePhrase"/>.</summary>
        public string LiveConfirmation { get; init; } = "";

        /// <summary>Dozwolone numery kont. Dla LIVE lista jest obowiązkowa. Dla DEMO pusta lista = dowolne konto demo.</summary>
        public IReadOnlyCollection<long> AllowedAccounts { get; init; } = Array.Empty<long>();

        /// <summary>Dozwolone nazwy brokerów (fragment nazwy, bez rozróżniania wielkości liter). Pusta = dowolny broker cTrader.</summary>
        public IReadOnlyCollection<string> AllowedBrokers { get; init; } = Array.Empty<string>();
    }

    public sealed class AccountGuardResult
    {
        public bool Allowed { get; init; }
        public bool IsLive { get; init; }
        public string Reason { get; init; } = "";
        public string ModeLabel => IsLive ? "LIVE (KONTO RZECZYWISTE)" : "DEMO";
    }

    /// <summary>
    /// Strażnik konta. Domyślnie bot handluje WYŁĄCZNIE na koncie DEMO.
    /// Handel na koncie LIVE wymaga jednocześnie: AllowLive = true, numeru konta na liście dozwolonych
    /// oraz wpisania dokładnej frazy potwierdzenia zawierającej numer konta. Brak któregokolwiek warunku = blokada.
    /// </summary>
    public static class AccountGuard
    {
        public static string RequiredLivePhrase(long accountNumber) => $"LIVE {accountNumber} AKCEPTUJE RYZYKO";

        public static AccountGuardResult Evaluate(AccountSnapshot? account, AccountGuardSettings settings)
        {
            if (account == null)
                return Deny(false, "Brak informacji o koncie – handel zablokowany.");
            if (account.Number <= 0)
                return Deny(account.IsLive, "Nieprawidłowy numer konta – handel zablokowany.");
            if (!account.IsValid)
                return Deny(account.IsLive, "Nieprawidłowe dane konta (saldo/kapitał) – handel zablokowany.");

            if (settings.AllowedBrokers.Count > 0)
            {
                bool brokerOk = settings.AllowedBrokers.Any(b => !string.IsNullOrWhiteSpace(b) &&
                    (account.BrokerName ?? "").IndexOf(b.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
                if (!brokerOk)
                    return Deny(account.IsLive, $"Broker '{account.BrokerName}' nie jest na liście dozwolonych ({string.Join(", ", settings.AllowedBrokers)}).");
            }

            if (!account.IsLive)
            {
                if (settings.AllowedAccounts.Count > 0 && !settings.AllowedAccounts.Contains(account.Number))
                    return Deny(false, $"Konto DEMO {account.Number} nie jest na liście dozwolonych kont.");
                return new AccountGuardResult { Allowed = true, IsLive = false, Reason = $"Konto DEMO {account.Number} ({account.BrokerName}) – handel dozwolony." };
            }

            // Konto LIVE – wszystkie warunki muszą być spełnione jednocześnie.
            if (!settings.AllowLive)
                return Deny(true, $"Wykryto konto LIVE {account.Number}. Handel na koncie rzeczywistym jest ZABLOKOWANY (parametr 'Zezwól na LIVE' = Nie).");
            if (!settings.AllowedAccounts.Contains(account.Number))
                return Deny(true, $"Konto LIVE {account.Number} nie zostało jawnie dodane do listy dozwolonych kont.");
            string required = RequiredLivePhrase(account.Number);
            if (!string.Equals((settings.LiveConfirmation ?? "").Trim(), required, StringComparison.Ordinal))
                return Deny(true, $"Brak poprawnego potwierdzenia LIVE. Wymagana fraza: \"{required}\".");

            return new AccountGuardResult { Allowed = true, IsLive = true, Reason = $"UWAGA: handel na koncie LIVE {account.Number} świadomie zatwierdzony przez użytkownika." };
        }

        private static AccountGuardResult Deny(bool isLive, string reason) => new() { Allowed = false, IsLive = isLive, Reason = reason };

        public static IReadOnlyCollection<long> ParseAccountList(string? csv)
        {
            var result = new List<long>();
            if (string.IsNullOrWhiteSpace(csv)) return result;
            foreach (var part in csv!.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (long.TryParse(part.Trim(), out var n) && n > 0) result.Add(n);
            return result;
        }

        public static IReadOnlyCollection<string> ParseList(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
            return csv!.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        }
    }
}
