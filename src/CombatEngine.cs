using System;
using System.Collections.Generic;
using System.Linq;

namespace KinojoMeterPrototype
{
    internal sealed class CombatSessionEngine
    {
        private readonly object _gate = new object();
        private readonly CharacterProfile _self;
        private readonly Dictionary<string, CombatRow> _participants = new Dictionary<string, CombatRow>(StringComparer.OrdinalIgnoreCase);
        private int _groupSize;
        private DateTime _startedAtUtc;
        private DateTime _lastEventUtc;
        private string _bossId = "";
        private string _bossName = "";
        private long _bossCurrentHp;
        private long _bossMaxHp;
        private bool _bossConfirmed;
        private bool _running;
        private bool _cleared;
        private string _contentKey = "";
        private string _contentName = "";
        private string _dungeonKey = "";
        private string _dungeonName = "";
        private string _difficultyKey = "";
        private string _difficultyName = "";
        private string _variantKey = "";
        private string _zoneId = "";
        private string _zoneName = "";
        private CaptureRuntimeInfo _runtime = new CaptureRuntimeInfo { CaptureMode = "ACTUAL", DecoderType = "BINARY_UNVALIDATED", DecoderVersion = "aion2-pending-fixture", UploadEligible = false };

        public event EventHandler SnapshotChanged;
        public event EventHandler EncounterCompleted;
        public event EventHandler<CombatRow> ParticipantChanged;
        public bool IsRunning { get { lock (_gate) return _running; } }

        public CombatSessionEngine(CharacterProfile self, int groupSize)
        {
            _self = self;
            _groupSize = NormalizeGroupSize(groupSize);
        }

        public void SetRuntimeInfo(CaptureRuntimeInfo runtime)
        {
            lock (_gate) _runtime = runtime ?? _runtime;
        }

        public void Reset()
        {
            lock (_gate)
            {
                _participants.Clear();
                _startedAtUtc = DateTime.MinValue;
                _lastEventUtc = DateTime.MinValue;
                _bossId = _bossName = "";
                _bossCurrentHp = _bossMaxHp = 0;
                _bossConfirmed = _running = _cleared = false;
            }
            RaiseSnapshotChanged();
        }

        public void Apply(CombatEvent value)
        {
            if (value == null) return;
            var completed = false;
            CombatRow changed = null;
            lock (_gate)
            {
                var timestamp = value.TimestampUtc == DateTime.MinValue ? DateTime.UtcNow : value.TimestampUtc;
                _lastEventUtc = timestamp;
                if (value.Kind == CombatEventKind.ZoneEntered || value.Kind == CombatEventKind.DungeonDetected)
                {
                    if (!String.IsNullOrWhiteSpace(value.ZoneId)) _zoneId = value.ZoneId;
                    if (!String.IsNullOrWhiteSpace(value.ZoneName)) _zoneName = value.ZoneName;
                    if (!String.IsNullOrWhiteSpace(value.ContentKey)) _contentKey = value.ContentKey;
                    if (!String.IsNullOrWhiteSpace(value.ContentName)) _contentName = value.ContentName;
                    if (!String.IsNullOrWhiteSpace(value.DungeonKey)) _dungeonKey = value.DungeonKey;
                    if (!String.IsNullOrWhiteSpace(value.DungeonName)) _dungeonName = value.DungeonName;
                    if (!String.IsNullOrWhiteSpace(value.DifficultyKey)) _difficultyKey = value.DifficultyKey;
                    if (!String.IsNullOrWhiteSpace(value.DifficultyName)) _difficultyName = value.DifficultyName;
                    if (!String.IsNullOrWhiteSpace(value.VariantKey)) _variantKey = value.VariantKey;
                }
                else if (value.Kind == CombatEventKind.PartyMember || value.Kind == CombatEventKind.LocalPlayer)
                {
                    changed = UpsertParticipant(value);
                    _groupSize = Math.Max(_groupSize, NormalizeGroupSize(Math.Max(_participants.Count, value.PartyNumber * 5)));
                }
                else if (value.Kind == CombatEventKind.BossSpawn)
                {
                    if (_cleared || (!String.IsNullOrWhiteSpace(_bossId) && !String.Equals(_bossId, value.TargetId, StringComparison.OrdinalIgnoreCase)))
                    {
                        _participants.Clear();
                        _startedAtUtc = DateTime.MinValue;
                    }
                    _bossId = value.TargetId ?? "";
                    _bossName = String.IsNullOrWhiteSpace(value.TargetName) ? "이름 없는 보스" : value.TargetName;
                    _bossMaxHp = Math.Max(0, value.MaxHp);
                    _bossCurrentHp = value.CurrentHp >= 0 ? value.CurrentHp : _bossMaxHp;
                    _bossConfirmed = value.IsBoss || _bossMaxHp > 0;
                    _running = false;
                    _cleared = false;
                }
                else if (value.Kind == CombatEventKind.BossHp)
                {
                    if (!String.IsNullOrWhiteSpace(value.TargetId)) _bossId = value.TargetId;
                    if (!String.IsNullOrWhiteSpace(value.TargetName)) _bossName = value.TargetName;
                    if (value.MaxHp > 0) _bossMaxHp = value.MaxHp;
                    if (value.CurrentHp >= 0) _bossCurrentHp = value.CurrentHp;
                    _bossConfirmed = _bossConfirmed || value.IsBoss || _bossMaxHp > 0;
                    if (_bossConfirmed && _bossCurrentHp > 0 && !_cleared) StartIfNeeded(timestamp);
                    if (_bossConfirmed && _bossMaxHp > 0 && _bossCurrentHp == 0)
                    {
                        _running = false;
                        _cleared = true;
                        completed = true;
                    }
                }
                else if (value.Kind == CombatEventKind.BossReset)
                {
                    _running = false;
                    _cleared = false;
                    _startedAtUtc = DateTime.MinValue;
                    _participants.Clear();
                    if (value.MaxHp > 0) _bossMaxHp = value.MaxHp;
                    _bossCurrentHp = value.CurrentHp > 0 ? value.CurrentHp : _bossMaxHp;
                }
                else if (value.Kind == CombatEventKind.Damage)
                {
                    if (value.Damage <= 0) return;
                    if (value.IsBoss && !_bossConfirmed)
                    {
                        _bossId = value.TargetId ?? "";
                        _bossName = String.IsNullOrWhiteSpace(value.TargetName) ? "이름 없는 보스" : value.TargetName;
                        _bossConfirmed = true;
                    }
                    if (_bossConfirmed && !_cleared) StartIfNeeded(timestamp);
                    changed = UpsertParticipant(value);
                    changed.TotalDamage += value.Damage;
                    changed.Dps = CalculateDps(changed, timestamp);
                }
                else if (value.Kind == CombatEventKind.EncounterEnd)
                {
                    _running = false;
                    _cleared = value.IsBoss || (_bossConfirmed && _bossMaxHp > 0 && _bossCurrentHp == 0);
                    completed = _cleared;
                }
                RecalculateShares(timestamp);
            }
            if (changed != null) RaiseParticipantChanged(Clone(changed));
            RaiseSnapshotChanged();
            if (completed) RaiseEncounterCompleted();
        }

        public void ApplyProfile(PartyProfileResult profile)
        {
            if (profile == null) return;
            CombatRow changed = null;
            lock (_gate)
            {
                if (!String.IsNullOrWhiteSpace(profile.ParticipantKey)) _participants.TryGetValue(profile.ParticipantKey, out changed);
                if (changed == null && !String.IsNullOrWhiteSpace(profile.PlatformCharacterId))
                    changed = _participants.Values.FirstOrDefault(row => String.Equals(row.PlatformCharacterId, profile.PlatformCharacterId, StringComparison.OrdinalIgnoreCase));
                if (changed == null && !String.IsNullOrWhiteSpace(profile.CharacterName))
                    changed = _participants.Values.FirstOrDefault(row => String.Equals(row.Name, profile.CharacterName, StringComparison.OrdinalIgnoreCase)
                        && (String.IsNullOrWhiteSpace(profile.ServerId) || String.Equals(row.ServerId, profile.ServerId, StringComparison.OrdinalIgnoreCase)));
                if (changed == null) return;
                if (!String.IsNullOrWhiteSpace(profile.PlatformCharacterId)) changed.PlatformCharacterId = profile.PlatformCharacterId;
                if (!String.IsNullOrWhiteSpace(profile.ServerId)) changed.ServerId = profile.ServerId;
                if (!String.IsNullOrWhiteSpace(profile.ServerName)) changed.ServerName = profile.ServerName;
                if (!String.IsNullOrWhiteSpace(profile.CharacterName)) changed.Name = profile.CharacterName;
                if (!String.IsNullOrWhiteSpace(profile.ClassKey)) changed.ClassKey = profile.ClassKey;
                if (!String.IsNullOrWhiteSpace(profile.ClassName)) changed.ClassName = profile.ClassName;
                if (!String.IsNullOrWhiteSpace(profile.ProfileImageUrl)) changed.ProfileImageUrl = profile.ProfileImageUrl;
                if (profile.PveCombatPower > 0) changed.CombatPower = profile.PveCombatPower;
                if (profile.ItemLevel > 0) changed.ItemLevel = profile.ItemLevel;
            }
            RaiseParticipantChanged(Clone(changed));
            RaiseSnapshotChanged();
        }

        public void ReplaceObservedParty(IEnumerable<CombatEvent> members)
        {
            var changed = new List<CombatRow>();
            lock (_gate)
            {
                foreach (var key in _participants.Keys
                    .Where(key => key.StartsWith("party-probe:", StringComparison.OrdinalIgnoreCase))
                    .ToList())
                    _participants.Remove(key);

                foreach (var member in members ?? Enumerable.Empty<CombatEvent>())
                {
                    if (member == null) continue;
                    changed.Add(Clone(UpsertParticipant(member)));
                }
                RecalculateShares(DateTime.UtcNow);
            }
            foreach (var row in changed) RaiseParticipantChanged(row);
            RaiseSnapshotChanged();
        }

        public void Tick(DateTime utcNow)
        {
            lock (_gate)
            {
                if (!_running) return;
                RecalculateShares(utcNow);
            }
            RaiseSnapshotChanged();
        }

        public CombatSnapshot Snapshot()
        {
            lock (_gate)
            {
                var rows = _participants.Values.Select(Clone).OrderBy(r => r.PartyNumber).ThenBy(r => r.PartySlot).ToList();
                if (_self != null && !rows.Any(r => r.IsSelf))
                {
                    rows.Insert(0, new CombatRow { ParticipantKey = _self.CharKey ?? _self.CharacterKey, PlatformCharacterId = _self.CharKey, PartyNumber = 1, PartySlot = 1, Name = _self.CharacterName, ServerId = _self.ServerId, ServerName = _self.ServerName, ClassKey = _self.ClassKey, ClassName = _self.ClassName, ClassRaw = 0, ProfileImageUrl = _self.ProfileImageUrl, CombatPower = _self.PveCombatPower, IsSelf = true });
                }
                var occupied = new HashSet<string>(rows.Select(r => r.PartyNumber + ":" + r.PartySlot));
                for (var i = 0; i < _groupSize; i++)
                {
                    var party = i / 5 + 1; var slot = i % 5 + 1;
                    if (!occupied.Contains(party + ":" + slot)) rows.Add(new CombatRow { PartyNumber = party, PartySlot = slot, Name = "빈 자리", IsEmpty = true });
                }
                return new CombatSnapshot
                {
                    StartedAtUtc = _startedAtUtc,
                    LastEventUtc = _lastEventUtc,
                    BossName = _bossName,
                    BossId = _bossId,
                    BossCurrentHp = _bossCurrentHp,
                    BossMaxHp = _bossMaxHp,
                    BossConfirmed = _bossConfirmed,
                    IsRunning = _running,
                    IsCleared = _cleared,
                    ContentKey = _contentKey,
                    ContentName = _contentName,
                    DungeonKey = _dungeonKey,
                    DungeonName = _dungeonName,
                    DifficultyKey = _difficultyKey,
                    DifficultyName = _difficultyName,
                    VariantKey = _variantKey,
                    ZoneId = _zoneId,
                    ZoneName = _zoneName,
                    CaptureEngine = _runtime.CaptureEngine,
                    CaptureMode = _runtime.CaptureMode,
                    DecoderType = _runtime.DecoderType,
                    DecoderVersion = _runtime.DecoderVersion,
                    DecoderValidated = _runtime.DecoderValidated,
                    UploadEligible = _runtime.UploadEligible,
                    Rows = rows.OrderBy(r => r.PartyNumber).ThenBy(r => r.PartySlot).ToList()
                };
            }
        }

        private void StartIfNeeded(DateTime timestamp)
        {
            if (_running) return;
            _running = true;
            if (_startedAtUtc == DateTime.MinValue) _startedAtUtc = timestamp;
        }

        private CombatRow UpsertParticipant(CombatEvent value)
        {
            var key = !String.IsNullOrWhiteSpace(value.ActorId) ? value.ActorId : (value.ActorServerId + ":" + value.ActorName);
            if (String.IsNullOrWhiteSpace(key)) key = "unknown-" + _participants.Count;
            CombatRow row;
            if (!_participants.TryGetValue(key, out row))
            {
                var slot = value.PartyNumber > 0 && value.PartySlot > 0 ? Tuple.Create(value.PartyNumber, value.PartySlot) : FindNextSlot();
                row = new CombatRow { ParticipantKey = key, PlatformCharacterId = value.PlatformCharacterId, PartyNumber = slot.Item1, PartySlot = slot.Item2, Name = value.ActorName ?? "알 수 없음", ServerId = value.ActorServerId ?? "", ServerName = value.ActorServer ?? "", ClassKey = value.ActorClassKey ?? "", ClassName = value.ActorClass ?? "", ClassRaw = value.ActorClassRaw, ProfileImageUrl = value.ProfileImageUrl ?? "", CombatPower = value.CombatPower, ItemLevel = value.ItemLevel, IsSelf = IsSelf(value), IsEmpty = false };
                _participants[key] = row;
            }
            if (!String.IsNullOrWhiteSpace(value.PlatformCharacterId)) row.PlatformCharacterId = value.PlatformCharacterId;
            if (!String.IsNullOrWhiteSpace(value.ActorName)) row.Name = value.ActorName;
            if (!String.IsNullOrWhiteSpace(value.ActorServerId)) row.ServerId = value.ActorServerId;
            if (!String.IsNullOrWhiteSpace(value.ActorServer)) row.ServerName = value.ActorServer;
            if (!String.IsNullOrWhiteSpace(value.ActorClassKey)) row.ClassKey = value.ActorClassKey;
            if (!String.IsNullOrWhiteSpace(value.ActorClass)) row.ClassName = value.ActorClass;
            if (value.ActorClassRaw > 0) row.ClassRaw = value.ActorClassRaw;
            if (!String.IsNullOrWhiteSpace(value.ProfileImageUrl)) row.ProfileImageUrl = value.ProfileImageUrl;
            if (value.CombatPower > 0) row.CombatPower = value.CombatPower;
            if (value.ItemLevel > 0) row.ItemLevel = value.ItemLevel;
            row.IsSelf = row.IsSelf || IsSelf(value);
            return row;
        }

        private bool IsSelf(CombatEvent value)
        {
            if (_self == null) return false;
            if (!String.IsNullOrWhiteSpace(_self.CharKey) && !String.IsNullOrWhiteSpace(value.ActorId) && String.Equals(_self.CharKey, value.ActorId, StringComparison.OrdinalIgnoreCase)) return true;
            return !String.IsNullOrWhiteSpace(value.ActorName) && String.Equals(_self.CharacterName, value.ActorName, StringComparison.OrdinalIgnoreCase);
        }

        private Tuple<int, int> FindNextSlot()
        {
            for (var i = 0; i < Math.Max(20, _groupSize); i++)
            {
                var party = i / 5 + 1; var slot = i % 5 + 1;
                if (!_participants.Values.Any(r => r.PartyNumber == party && r.PartySlot == slot)) return Tuple.Create(party, slot);
            }
            return Tuple.Create(1, 1);
        }

        private void RecalculateShares(DateTime timestamp)
        {
            var total = Math.Max(1L, _participants.Values.Sum(r => r.TotalDamage));
            foreach (var row in _participants.Values)
            {
                row.Dps = CalculateDps(row, timestamp);
                row.Share = row.TotalDamage * 100.0 / total;
            }
        }

        private long CalculateDps(CombatRow row, DateTime timestamp)
        {
            var first = _startedAtUtc == DateTime.MinValue ? timestamp : _startedAtUtc;
            var seconds = Math.Max(1.0, (timestamp - first).TotalSeconds);
            return (long)Math.Round(row.TotalDamage / seconds);
        }

        private static int NormalizeGroupSize(int size)
        {
            if (size <= 5) return 5;
            if (size <= 10) return 10;
            if (size <= 15) return 15;
            return 20;
        }

        private static CombatRow Clone(CombatRow row)
        {
            return new CombatRow { ParticipantKey = row.ParticipantKey, PlatformCharacterId = row.PlatformCharacterId, PartyNumber = row.PartyNumber, PartySlot = row.PartySlot, Name = row.Name, ServerId = row.ServerId, ServerName = row.ServerName, ClassKey = row.ClassKey, ClassName = row.ClassName, ClassRaw = row.ClassRaw, ProfileImageUrl = row.ProfileImageUrl, CombatPower = row.CombatPower, ItemLevel = row.ItemLevel, TotalDamage = row.TotalDamage, Dps = row.Dps, Share = row.Share, IsSelf = row.IsSelf, IsEmpty = row.IsEmpty };
        }

        private void RaiseSnapshotChanged() { SnapshotChanged?.Invoke(this, EventArgs.Empty); }
        private void RaiseEncounterCompleted() { EncounterCompleted?.Invoke(this, EventArgs.Empty); }
        private void RaiseParticipantChanged(CombatRow row) { ParticipantChanged?.Invoke(this, row); }
    }
}
