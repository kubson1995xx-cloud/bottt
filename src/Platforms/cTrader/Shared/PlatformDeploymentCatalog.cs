using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Apex.Core.Bridge;

namespace Apex.Platforms.CTrader;

public static class PlatformDeploymentCatalog
{
    private sealed record Catalog(string Broker, long Account, string Currency, List<string> Labels);
    // SDK main thread. Known legacy defaults + exact configured label + permanent catalog.
    // Cannot discover erased storage or pre-catalog arbitrary labels; never claim rollback proof.
    public static List<PlatformStateInventory> Inventory(string broker, long account, string currency, string label, Func<string, string?> read, Action<string, string> write)
    {
        try
        {
            string key = PlatformSafety.StorageKey("BridgeCatalog:" + currency, broker, account, "");
            string? saved = read(key);
            var catalog = saved == null ? new Catalog(broker, account, currency, new()) : JsonSerializer.Deserialize<Catalog>(saved);
            if (catalog == null || catalog.Broker != broker || catalog.Account != account || catalog.Currency != currency || catalog.Labels == null
                || catalog.Labels.Count > 1000 || catalog.Labels.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("invalid deployment catalog");
            var inventory = new List<PlatformStateInventory>();
            foreach (string old in catalog.Labels.Concat(new[] { "APEX", "APEX-N", label }).Distinct(StringComparer.Ordinal))
            {
                string journalKey = PlatformSafety.StorageKey("Bridge", broker, account, old);
                string? journal = read(journalKey);
                bool missing = journal == null && catalog.Labels.Contains(old);
                var parsed = new CommandJournal(journal, _ => throw new InvalidOperationException("read-only inventory"));
                inventory.Add(new PlatformStateInventory(old, parsed.Healthy, parsed.Count, missing));
            }
            if (inventory.Any(x => !x.Healthy || x.Missing)) return inventory;
            if (!catalog.Labels.Contains(label))
            {
                // Refuse a new namespace in the presence of any old reservations, even terminal
                // journal entries: migration is explicit and never implemented by deleting evidence.
                if (inventory.Any(x => x.Label != label && x.Entries > 0)) return inventory;
                string currentKey = PlatformSafety.StorageKey("Bridge", broker, account, label);
                if (read(currentKey) == null) write(currentKey, "{}");
                catalog.Labels.Add(label); write(key, JsonSerializer.Serialize(catalog));
            }
            return inventory;
        }
        catch { return new() { new PlatformStateInventory(label, false, -1, true) }; }
    }
}
