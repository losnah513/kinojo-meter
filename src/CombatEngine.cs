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
        private DateTime _lastDamageUtc;
        private DateTime _endedAtUtc;
        private string _bossId = "";
        private long _bossRuntimeId;
        private string _bossName = "";
        private long _bossCurrentHp;
        private long _bossMaxHp;
        private int _bossOrder;
        private string _bossIdentityMode = "";
        private string _bossHpSource = "";
        private string _completionMode = "";
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
        private readonly HashSet<string> _rosterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _lastPartialRosterSignature = "";
        private int _partialRosterConfirmations;
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
                _lastDamageUtc = DateTime.MinValue;
                _endedAtUtc = DateTime.MinValue;
                _bossId = _bossName = "";
                _bossRuntimeId = 0;
                _bossCurrentHp = _bossMaxHp = 0;
                _bossOrder = 0;
                _bossIdentityMode = _bossHpSource = "";
                _completionMode = "";
                _bossConfirmed = _running = _cleared = false;
                _rosterNames.Clear();
                _lastPartialRosterSignature = "";
                _partialRosterConfirmations = 0;
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
                var combatClockEvent = value.Kind == CombatEventKind.Damage ||
                    value.Kind == CombatEventKind.BossHp || value.Kind == CombatEventKind.BossSpawn ||
                    value.Kind == CombatEventKind.BossReset || value.Kind == CombatEventKind.EncounterEnd;
                if (!_cleared || combatClockEvent) _lastEventUtc = timestamp;
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
                else if (value.Kind == CombatEventKind.EntityIdentity)
                {
                    changed = ApplyEntityIdentity(value);
                    if (String.Equals(_bossId, value.ActorId, StringComparison.OrdinalIgnoreCase) && !String.IsNullOrWhiteSpace(value.ActorName))
                        _bossName = value.ActorName;
                }
                else if (value.Kind == CombatEventKind.BossSpawn)
                {
                    if (_cleared || (!String.IsNullOrWhiteSpace(_bossId) && !String.Equals(_bossId, value.TargetId, StringComparison.OrdinalIgnoreCase)))
                    {
                        _participants.Clear();
                        _startedAtUtc = DateTime.MinValue;
                    }
                    _bossId = value.TargetId ?? "";
                    _bossRuntimeId = value.TargetRuntimeId;
                    _bossName = String.IsNullOrWhiteSpace(value.TargetName) ? "이름 없는 보스" : value.TargetName;
                    _bossMaxHp = Math.Max(0, value.MaxHp);
                    _bossCurrentHp = value.CurrentHp >= 0 ? value.CurrentHp : _bossMaxHp;
                    _bossOrder = value.BossOrder;
                    _bossIdentityMode = value.BossIdentityMode ?? "";
                    _bossHpSource = value.MaxHp > 0 ? "OBSERVED_CURRENT_MAX" : "";
                    _bossConfirmed = value.IsBoss || _bossMaxHp > 0;
                    _running = false;
                    _cleared = false;
                    _completionMode = "";
                }
                else if (value.Kind == CombatEventKind.BossHp)
                {
                    var newTarget = !String.IsNullOrWhiteSpace(value.TargetId) &&
                        !String.IsNullOrWhiteSpace(_bossId) &&
                        !String.Equals(_bossId, value.TargetId, StringComparison.OrdinalIgnoreCase);
                    var sameBossOrderPhase = newTarget && !_cleared && value.BossOrder > 0 && value.BossOrder == _bossOrder;
                    if (newTarget && !sameBossOrderPhase && (_cleared || !_running))
                    {
                        ResetEncounterDamageLocked();
                        _lastEventUtc = timestamp;
                    }
                    if (!String.IsNullOrWhiteSpace(value.TargetId)) _bossId = value.TargetId;
                    if (value.TargetRuntimeId > 0) _bossRuntimeId = value.TargetRuntimeId;
                    if (!String.IsNullOrWhiteSpace(value.TargetName)) _bossName = value.TargetName;
                    if (value.MaxHp > _bossMaxHp) _bossMaxHp = value.MaxHp;
                    if (value.CurrentHp >= 0) _bossCurrentHp = value.CurrentHp;
                    if (value.BossOrder > 0) _bossOrder = value.BossOrder;
                    if (!String.IsNullOrWhiteSpace(value.BossIdentityMode)) _bossIdentityMode = value.BossIdentityMode;
                    if (_bossMaxHp > 0) _bossHpSource = "OBSERVED_CURRENT_MAX";
                    _bossConfirmed = _bossConfirmed || value.IsBoss || _bossMaxHp > 0;
                    if (_bossConfirmed && _bossCurrentHp > 0 && !_cleared) StartIfNeeded(timestamp);
                    if (_bossConfirmed && _bossMaxHp > 0 && _bossCurrentHp == 0 && !_cleared)
                    {
                        _running = false;
                        _cleared = true;
                        _completionMode = "HP_ZERO";
                        _endedAtUtc = timestamp;
                        completed = true;
                    }
                }
                else if (value.Kind == CombatEventKind.BossReset)
                {
                    _running = false;
                    _cleared = false;
                    _completionMode = "";
                    _startedAtUtc = DateTime.MinValue;
                    _participants.Clear();
                    if (value.MaxHp > 0) _bossMaxHp = value.MaxHp;
                    _bossCurrentHp = value.CurrentHp > 0 ? value.CurrentHp : _bossMaxHp;
                }
                else if (value.Kind == CombatEventKind.Damage)
                {
                    if (value.Damage <= 0) return;
                    var separated = _lastDamageUtc != DateTime.MinValue && (timestamp - _lastDamageUtc) >= TimeSpan.FromSeconds(12);
                    var sameConfirmedTarget = _bossConfirmed && _bossCurrentHp > 0 &&
                        !String.IsNullOrWhiteSpace(value.TargetId) &&
                        String.Equals(_bossId, value.TargetId, StringComparison.OrdinalIgnoreCase);
                    var sameBossOrderPhase = !_cleared && value.BossOrder > 0 && value.BossOrder == _bossOrder;
                    var newConfirmedTarget = value.IsBoss && !String.IsNullOrWhiteSpace(value.TargetId) &&
                        !String.IsNullOrWhiteSpace(_bossId) && !String.Equals(_bossId, value.TargetId, StringComparison.OrdinalIgnoreCase) &&
                        !sameBossOrderPhase;
                    var newTargetAfterClear = _cleared;
                    if ((separated && !sameConfirmedTarget && !sameBossOrderPhase) || newConfirmedTarget || newTargetAfterClear)
                    {
                        ResetEncounterDamageLocked();
                        _lastEventUtc = timestamp;
                    }
                    if (!String.IsNullOrWhiteSpace(value.TargetId)) _bossId = value.TargetId;
                    if (value.TargetRuntimeId > 0) _bossRuntimeId = value.TargetRuntimeId;
                    if (!String.IsNullOrWhiteSpace(value.TargetName)) _bossName = value.TargetName;
                    if (value.BossOrder > 0) _bossOrder = value.BossOrder;
                    if (!String.IsNullOrWhiteSpace(value.BossIdentityMode)) _bossIdentityMode = value.BossIdentityMode;
                    if (String.IsNullOrWhiteSpace(_bossName)) _bossName = "전투 대상";
                    if (value.IsBoss && !_bossConfirmed)
                    {
                        _bossConfirmed = true;
                    }
                    _cleared = false;
                    _completionMode = "";
                    StartIfNeeded(timestamp);
                    _lastDamageUtc = timestamp;
                    changed = UpsertParticipant(value);
                    changed.TotalDamage += value.Damage;
                    changed.Dps = CalculateDps(changed, timestamp);
                }
                else if (value.Kind == CombatEventKind.EncounterEnd)
                {
                    _running = false;
                    _cleared = value.IsBoss || (_bossConfirmed && _bossMaxHp > 0 && _bossCurrentHp == 0);
                    if (_cleared)
                    {
                        _completionMode = "EXPLICIT_EVENT";
                        _endedAtUtc = timestamp;
                    }
                    completed = _cleared;
                }
                RecalculateShares(_cleared && _endedAtUtc != DateTime.MinValue ? _endedAtUtc : timestamp);
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
                var incoming = (members ?? Enumerable.Empty<CombatEvent>())
                    .Where(member => member != null && !String.IsNullOrWhiteSpace(member.ActorName))
                    .GroupBy(member => member.ActorName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                var incomingNames = new HashSet<string>(incoming.Select(member => member.ActorName.Trim()), StringComparer.OrdinalIgnoreCase);
                var partial = _rosterNames.Count > 0 && incomingNames.Count < _rosterNames.Count && _rosterNames.Except(incomingNames).Any();
                var signature = String.Join("|", incomingNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
                if (partial)
                {
                    if (String.Equals(_lastPartialRosterSignature, signature, StringComparison.OrdinalIgnoreCase)) _partialRosterConfirmations++;
                    else
                    {
                        _lastPartialRosterSignature = signature;
                        _partialRosterConfirmations = 1;
                    }
                }
                else
                {
                    _lastPartialRosterSignature = "";
                    _partialRosterConfirmations = 0;
                }

                foreach (var member in incoming)
                    changed.Add(Clone(UpsertParticipant(member)));

                // A bus party can legitimately have four zero-damage passengers waiting at
                // the final boss. A truncated roster must never evict them merely because
                // they have not dealt damage. A complete same-size replacement can still
                // replace members by name; partial observations only refresh what was seen.
                if (!partial)
                {
                    foreach (var missingName in _rosterNames.Except(incomingNames).ToList())
                    {
                        var missing = _participants.Values.FirstOrDefault(row => !row.IsEmpty && String.Equals(row.Name, missingName, StringComparison.OrdinalIgnoreCase));
                        if (missing != null && !missing.IsSelf)
                        {
                            var missingKey = _participants.First(pair => Object.ReferenceEquals(pair.Value, missing)).Key;
                            _participants.Remove(missingKey);
                        }
                    }
                    _rosterNames.Clear();
                    foreach (var name in incomingNames) _rosterNames.Add(name);
                }
                else
                {
                    foreach (var name in incomingNames) _rosterNames.Add(name);
                }
                RecalculateShares(_cleared && _endedAtUtc != DateTime.MinValue ? _endedAtUtc : DateTime.UtcNow);
            }
            foreach (var row in changed) RaiseParticipantChanged(row);
            RaiseSnapshotChanged();
        }

        public void Tick(DateTime utcNow)
        {
            lock (_gate)
            {
                if (!_running) return;
                if (_lastDamageUtc != DateTime.MinValue && (utcNow - _lastDamageUtc) >= TimeSpan.FromSeconds(12))
                {
                    _running = false;
                    _cleared = false;
                    if (_bossConfirmed) _completionMode = "PHASE_IDLE_12S";
                }
                RecalculateShares(utcNow);
            }
            RaiseSnapshotChanged();
        }

        public void ApplyClassMapping(int classRaw, string classKey, string className)
        {
            if (classRaw <= 0 || (String.IsNullOrWhiteSpace(classKey) && String.IsNullOrWhiteSpace(className))) return;
            var changed = new List<CombatRow>();
            lock (_gate)
            {
                foreach (var row in _participants.Values.Where(value => value.ClassRaw == classRaw))
                {
                    if (!String.IsNullOrWhiteSpace(classKey)) row.ClassKey = classKey;
                    if (!String.IsNullOrWhiteSpace(className)) row.ClassName = className;
                    changed.Add(Clone(row));
                }
            }
            foreach (var row in changed) RaiseParticipantChanged(row);
            if (changed.Count > 0) RaiseSnapshotChanged();
        }

        public bool FinalizeCurrentEncounter(string completionMode, DateTime timestampUtc)
        {
            var completed = false;
            lock (_gate)
            {
                if (!_bossConfirmed || _cleared || _startedAtUtc == DateTime.MinValue) return false;
                _running = false;
                _cleared = true;
                _completionMode = String.IsNullOrWhiteSpace(completionMode) ? "INFERRED_NEXT_BOSS" : completionMode;
                _endedAtUtc = _lastDamageUtc != DateTime.MinValue ? _lastDamageUtc :
                    (timestampUtc == DateTime.MinValue ? DateTime.UtcNow : timestampUtc);
                _lastEventUtc = _endedAtUtc;
                RecalculateShares(_endedAtUtc);
                completed = true;
            }
            RaiseSnapshotChanged();
            if (completed) RaiseEncounterCompleted();
            return completed;
        }

        public CombatSnapshot Snapshot()
        {
            lock (_gate)
            {
                var rows = _participants.Values.Select(Clone).OrderBy(r => r.PartyNumber).ThenBy(r => r.PartySlot).ToList();
                if (_self != null && !rows.Any(r => r.IsSelf))
                {
                    rows.Insert(0, new CombatRow { ParticipantKey = _self.CharKey ?? _self.CharacterKey, PlatformCharacterId = _self.CharKey, PartyNumber = 1, PartySlot = 1, Name = _self.CharacterName, ServerId = _self.ServerId, ServerName = _self.ServerName, ServerRaw = 0, ClassKey = _self.ClassKey, ClassName = _self.ClassName, ClassRaw = 0, ProfileImageUrl = _self.ProfileImageUrl, CombatPower = _self.PveCombatPower, IsSelf = true });
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
                    LastEventUtc = _cleared && _endedAtUtc != DateTime.MinValue ? _endedAtUtc : _lastEventUtc,
                    BossName = _bossName,
                    BossId = _bossId,
                    BossRuntimeId = _bossRuntimeId,
                    BossCurrentHp = _bossCurrentHp,
                    BossMaxHp = _bossMaxHp,
                    BossOrder = _bossOrder,
                    BossIdentityMode = _bossIdentityMode,
                    BossHpSource = _bossHpSource,
                    CompletionMode = _completionMode,
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
                if (!String.IsNullOrWhiteSpace(value.ActorName))
                    row = _participants.Values.FirstOrDefault(candidate => !candidate.IsEmpty && String.Equals(candidate.Name, value.ActorName, StringComparison.OrdinalIgnoreCase));
                if (row != null)
                {
                    var previousKey = _participants.First(pair => Object.ReferenceEquals(pair.Value, row)).Key;
                    _participants.Remove(previousKey);
                    row.ParticipantKey = key;
                    _participants[key] = row;
                }
            }
            if (row == null)
            {
                var slot = value.PartyNumber > 0 && value.PartySlot > 0 ? Tuple.Create(value.PartyNumber, value.PartySlot) : FindNextSlot();
                row = new CombatRow { ParticipantKey = key, PlatformCharacterId = value.PlatformCharacterId, PartyNumber = slot.Item1, PartySlot = slot.Item2, Name = value.ActorName ?? "알 수 없음", ServerId = value.ActorServerId ?? "", ServerName = value.ActorServer ?? "", ServerRaw = value.ActorServerRaw, ClassKey = value.ActorClassKey ?? "", ClassName = value.ActorClass ?? "", ClassRaw = value.ActorClassRaw, ProfileImageUrl = value.ProfileImageUrl ?? "", CombatPower = value.CombatPower, ItemLevel = value.ItemLevel, IsSelf = IsSelf(value), IsEmpty = false };
                _participants[key] = row;
            }
            if (!String.IsNullOrWhiteSpace(value.PlatformCharacterId)) row.PlatformCharacterId = value.PlatformCharacterId;
            if (!String.IsNullOrWhiteSpace(value.ActorName)) row.Name = value.ActorName;
            if (!String.IsNullOrWhiteSpace(value.ActorServerId)) row.ServerId = value.ActorServerId;
            if (!String.IsNullOrWhiteSpace(value.ActorServer)) row.ServerName = value.ActorServer;
            if (value.ActorServerRaw > 0) row.ServerRaw = value.ActorServerRaw;
            if (!String.IsNullOrWhiteSpace(value.ActorClassKey)) row.ClassKey = value.ActorClassKey;
            if (!String.IsNullOrWhiteSpace(value.ActorClass)) row.ClassName = value.ActorClass;
            if (value.ActorClassRaw > 0) row.ClassRaw = value.ActorClassRaw;
            if (!String.IsNullOrWhiteSpace(value.ProfileImageUrl)) row.ProfileImageUrl = value.ProfileImageUrl;
            if (value.CombatPower > 0) row.CombatPower = value.CombatPower;
            if (value.ItemLevel > 0) row.ItemLevel = value.ItemLevel;
            row.IsSelf = row.IsSelf || IsSelf(value);
            if (row.IsSelf && _self != null)
            {
                if (String.IsNullOrWhiteSpace(row.Name)) row.Name = _self.CharacterName;
                if (String.IsNullOrWhiteSpace(row.PlatformCharacterId)) row.PlatformCharacterId = _self.CharKey;
                if (String.IsNullOrWhiteSpace(row.ServerId)) row.ServerId = _self.ServerId;
                if (String.IsNullOrWhiteSpace(row.ServerName)) row.ServerName = _self.ServerName;
                if (String.IsNullOrWhiteSpace(row.ClassKey)) row.ClassKey = _self.ClassKey;
                if (String.IsNullOrWhiteSpace(row.ClassName)) row.ClassName = _self.ClassName;
                if (String.IsNullOrWhiteSpace(row.ProfileImageUrl)) row.ProfileImageUrl = _self.ProfileImageUrl;
                if (row.CombatPower <= 0) row.CombatPower = _self.PveCombatPower;
            }
            return row;
        }

        private CombatRow ApplyEntityIdentity(CombatEvent value)
        {
            CombatRow row;
            if (!String.IsNullOrWhiteSpace(value.ActorId) && _participants.TryGetValue(value.ActorId, out row))
            {
                if (!String.IsNullOrWhiteSpace(value.ActorName)) row.Name = value.ActorName;
                row.IsSelf = row.IsSelf || IsSelf(value);
                return row;
            }
            row = _participants.Values.FirstOrDefault(candidate => !candidate.IsEmpty &&
                !String.IsNullOrWhiteSpace(value.ActorName) && String.Equals(candidate.Name, value.ActorName, StringComparison.OrdinalIgnoreCase));
            if (row == null && IsSelf(value)) return UpsertParticipant(value);
            if (row == null) return null;
            var oldKey = _participants.First(pair => Object.ReferenceEquals(pair.Value, row)).Key;
            if (!String.IsNullOrWhiteSpace(value.ActorId) && !String.Equals(oldKey, value.ActorId, StringComparison.OrdinalIgnoreCase))
            {
                _participants.Remove(oldKey);
                row.ParticipantKey = value.ActorId;
                _participants[value.ActorId] = row;
            }
            return row;
        }

        private void ResetEncounterDamageLocked()
        {
            foreach (var row in _participants.Values)
            {
                row.TotalDamage = 0;
                row.Dps = 0;
                row.Share = 0;
            }
            _startedAtUtc = DateTime.MinValue;
            _lastEventUtc = DateTime.MinValue;
            _lastDamageUtc = DateTime.MinValue;
            _endedAtUtc = DateTime.MinValue;
            _bossId = "";
            _bossRuntimeId = 0;
            _bossName = "";
            _bossCurrentHp = 0;
            _bossMaxHp = 0;
            _bossOrder = 0;
            _bossIdentityMode = _bossHpSource = "";
            _completionMode = "";
            _bossConfirmed = false;
            _running = false;
            _cleared = false;
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
            return new CombatRow { ParticipantKey = row.ParticipantKey, PlatformCharacterId = row.PlatformCharacterId, PartyNumber = row.PartyNumber, PartySlot = row.PartySlot, Name = row.Name, ServerId = row.ServerId, ServerName = row.ServerName, ServerRaw = row.ServerRaw, ClassKey = row.ClassKey, ClassName = row.ClassName, ClassRaw = row.ClassRaw, ProfileImageUrl = row.ProfileImageUrl, CombatPower = row.CombatPower, ItemLevel = row.ItemLevel, TotalDamage = row.TotalDamage, Dps = row.Dps, Share = row.Share, IsSelf = row.IsSelf, IsEmpty = row.IsEmpty };
        }

        private void RaiseSnapshotChanged() { SnapshotChanged?.Invoke(this, EventArgs.Empty); }
        private void RaiseEncounterCompleted() { EncounterCompleted?.Invoke(this, EventArgs.Empty); }
        private void RaiseParticipantChanged(CombatRow row) { ParticipantChanged?.Invoke(this, row); }
    }
}
