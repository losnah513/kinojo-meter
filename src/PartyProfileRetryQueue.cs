using System;
using System.Collections.Generic;
using System.Linq;

namespace KinojoMeterPrototype
{
    internal sealed class PartyProfileRetryQueue
    {
        private sealed class RetryEntry
        {
            public CombatRow Row;
            public DateTime NextAttemptUtc;
            public bool InFlight;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, RetryEntry> _entries = new Dictionary<string, RetryEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _retryInterval;

        public PartyProfileRetryQueue(TimeSpan retryInterval)
        {
            _retryInterval = retryInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(25) : retryInterval;
        }

        public bool TryBegin(CombatRow row, DateTime utcNow, out string key)
        {
            key = KeyFor(row);
            if (String.IsNullOrWhiteSpace(key) || row == null || row.IsEmpty) return false;
            lock (_gate)
            {
                RetryEntry entry;
                if (!_entries.TryGetValue(key, out entry))
                {
                    entry = new RetryEntry { Row = Clone(row), NextAttemptUtc = DateTime.MinValue };
                    _entries[key] = entry;
                }
                else entry.Row = Clone(row);
                if (entry.InFlight || utcNow < entry.NextAttemptUtc) return false;
                entry.InFlight = true;
                return true;
            }
        }

        public void Complete(string key, CombatRow row, bool resolved, DateTime utcNow)
        {
            if (String.IsNullOrWhiteSpace(key)) return;
            lock (_gate)
            {
                if (resolved)
                {
                    _entries.Remove(key);
                    return;
                }
                RetryEntry entry;
                if (!_entries.TryGetValue(key, out entry))
                {
                    entry = new RetryEntry();
                    _entries[key] = entry;
                }
                entry.Row = Clone(row);
                entry.InFlight = false;
                entry.NextAttemptUtc = utcNow + _retryInterval;
            }
        }

        public IList<CombatRow> TakeDue(DateTime utcNow, IEnumerable<CombatRow> activeRows)
        {
            var active = new HashSet<string>((activeRows ?? Enumerable.Empty<CombatRow>()).Select(KeyFor)
                .Where(key => !String.IsNullOrWhiteSpace(key)), StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                foreach (var key in _entries.Where(pair => !pair.Value.InFlight && !active.Contains(pair.Key)).Select(pair => pair.Key).ToList())
                    _entries.Remove(key);
                return _entries.Values.Where(entry => !entry.InFlight && utcNow >= entry.NextAttemptUtc)
                    .Select(entry => Clone(entry.Row)).Where(row => row != null).ToList();
            }
        }

        public void Clear()
        {
            lock (_gate) _entries.Clear();
        }

        internal static string KeyFor(CombatRow row)
        {
            if (row == null) return "";
            if (!String.IsNullOrWhiteSpace(row.PlatformCharacterId)) return "platform:" + row.PlatformCharacterId.Trim();
            if (!String.IsNullOrWhiteSpace(row.ServerId)) return "server:" + row.ServerId.Trim() + ":" + (row.Name ?? "").Trim();
            if (row.ServerRaw > 0) return "server-raw:" + row.ServerRaw + ":" + (row.Name ?? "").Trim();
            if (!String.IsNullOrWhiteSpace(row.ServerName)) return "server-name:" + row.ServerName.Trim() + ":" + (row.Name ?? "").Trim();
            if (!String.IsNullOrWhiteSpace(row.ParticipantKey)) return "participant:" + row.ParticipantKey.Trim();
            return String.IsNullOrWhiteSpace(row.Name) ? "" : "name:" + row.Name.Trim();
        }

        private static CombatRow Clone(CombatRow row)
        {
            if (row == null) return null;
            return new CombatRow
            {
                ParticipantKey = row.ParticipantKey,
                PlatformCharacterId = row.PlatformCharacterId,
                PartyNumber = row.PartyNumber,
                PartySlot = row.PartySlot,
                Name = row.Name,
                ServerId = row.ServerId,
                ServerName = row.ServerName,
                ServerRaw = row.ServerRaw,
                ClassKey = row.ClassKey,
                ClassName = row.ClassName,
                ClassRaw = row.ClassRaw,
                ProfileImageUrl = row.ProfileImageUrl,
                CombatPower = row.CombatPower,
                ItemLevel = row.ItemLevel,
                TotalDamage = row.TotalDamage,
                Dps = row.Dps,
                Share = row.Share,
                IsSelf = row.IsSelf,
                IsEmpty = row.IsEmpty
            };
        }
    }
}
