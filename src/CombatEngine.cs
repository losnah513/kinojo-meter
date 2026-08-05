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
        private readonly HashSet<string> _rosterParticipantKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                _rosterParticipantKeys.Clear();
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
                    _bossHpSource = value.BossHpSource ?? "";
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
                    if (!String.IsNullOrWhiteSpace(value.BossHpSource)) _bossHpSource = value.BossHpSource;
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
                if (changed == null) changed = FindParticipantForProfileLocked(profile);
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

        public void ReplaceObservedParty(IEnumerable<CombatEvent> members, string evidence = null)
        {
            var changed = new List<CombatRow>();
            lock (_gate)
            {
                var incoming = (members ?? Enumerable.Empty<CombatEvent>())
                    .Where(member => member != null && !String.IsNullOrWhiteSpace(member.ActorName))
                    .GroupBy(BuildRosterIdentity, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                var incomingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var member in incoming)
                {
                    var row = UpsertParticipant(member);
                    incomingKeys.Add(row.ParticipantKey);
                    changed.Add(Clone(row));
                }

                var hasMissing = _rosterParticipantKeys.Except(incomingKeys).Any();
                var partial = _rosterParticipantKeys.Count > 0 && incomingKeys.Count < _rosterParticipantKeys.Count && hasMissing;
                var replacementAllowed = !hasMissing || (!HasEncounterActivityLocked() && (!partial || IsConfirmedRosterEvidence(evidence)));

                // A bus party can legitimately have four zero-damage passengers waiting at
                // the final boss. Only an independently confirmed pre-combat small/solo roster
                // may converge a shrink; truncated or in-combat observations remain upserts.
                if (replacementAllowed)
                {
                    foreach (var missingKey in _rosterParticipantKeys.Except(incomingKeys).ToList())
                    {
                        CombatRow missing;
                        if (_participants.TryGetValue(missingKey, out missing) && !missing.IsSelf) _participants.Remove(missingKey);
                    }
                    _rosterParticipantKeys.Clear();
                    foreach (var key in incomingKeys) _rosterParticipantKeys.Add(key);
                }
                else
                {
                    foreach (var key in incomingKeys) _rosterParticipantKeys.Add(key);
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
            var key = BuildParticipantKey(value);
            if (String.IsNullOrWhiteSpace(key)) key = "unknown-" + _participants.Count;
            CombatRow row;
            if (!_participants.TryGetValue(key, out row))
            {
                row = FindParticipantLocked(value);
                if (row != null)
                {
                    var previousKey = _participants.First(pair => Object.ReferenceEquals(pair.Value, row)).Key;
                    _participants.Remove(previousKey);
                    row.ParticipantKey = key;
                    _participants[key] = row;
                    ReplaceRosterKeyLocked(previousKey, key);
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
            row = FindParticipantLocked(value);
            if (row == null && IsSelf(value)) return UpsertParticipant(value);
            if (row == null) return null;
            var oldKey = _participants.First(pair => Object.ReferenceEquals(pair.Value, row)).Key;
            if (!String.IsNullOrWhiteSpace(value.ActorId) && !String.Equals(oldKey, value.ActorId, StringComparison.OrdinalIgnoreCase))
            {
                _participants.Remove(oldKey);
                row.ParticipantKey = value.ActorId;
                _participants[value.ActorId] = row;
                ReplaceRosterKeyLocked(oldKey, value.ActorId);
            }
            return row;
        }

        private CombatRow FindParticipantLocked(CombatEvent value)
        {
            if (value == null || String.IsNullOrWhiteSpace(value.ActorName)) return null;
            var candidates = _participants.Values.Where(candidate => !candidate.IsEmpty &&
                String.Equals(candidate.Name, value.ActorName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!String.IsNullOrWhiteSpace(value.PlatformCharacterId))
            {
                var byPlatform = candidates.FirstOrDefault(candidate => String.Equals(candidate.PlatformCharacterId, value.PlatformCharacterId, StringComparison.OrdinalIgnoreCase));
                if (byPlatform != null) return byPlatform;
            }
            if (!String.IsNullOrWhiteSpace(value.ActorServerId))
            {
                var byServerId = candidates.FirstOrDefault(candidate => String.Equals(candidate.ServerId, value.ActorServerId, StringComparison.OrdinalIgnoreCase));
                if (byServerId != null) return byServerId;
                return null;
            }
            if (value.ActorServerRaw > 0)
            {
                var byServerRaw = candidates.FirstOrDefault(candidate => candidate.ServerRaw == value.ActorServerRaw);
                if (byServerRaw != null) return byServerRaw;
                return null;
            }
            if (!String.IsNullOrWhiteSpace(value.ActorServer))
            {
                var byServerName = candidates.FirstOrDefault(candidate => String.Equals(candidate.ServerName, value.ActorServer, StringComparison.OrdinalIgnoreCase));
                if (byServerName != null) return byServerName;
                return null;
            }
            return candidates.Count == 1 ? candidates[0] : null;
        }

        private CombatRow FindParticipantForProfileLocked(PartyProfileResult profile)
        {
            if (profile == null || String.IsNullOrWhiteSpace(profile.CharacterName)) return null;
            var candidates = _participants.Values.Where(row => !row.IsEmpty &&
                String.Equals(row.Name, profile.CharacterName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!String.IsNullOrWhiteSpace(profile.ServerId))
                return candidates.FirstOrDefault(row => String.Equals(row.ServerId, profile.ServerId, StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(profile.ServerName))
                return candidates.FirstOrDefault(row => String.Equals(row.ServerName, profile.ServerName, StringComparison.OrdinalIgnoreCase));
            return candidates.Count == 1 ? candidates[0] : null;
        }

        private static string BuildParticipantKey(CombatEvent value)
        {
            if (value == null) return "";
            if (!String.IsNullOrWhiteSpace(value.ActorId)) return value.ActorId;
            if (!String.IsNullOrWhiteSpace(value.PlatformCharacterId)) return "platform:" + value.PlatformCharacterId.Trim();
            if (!String.IsNullOrWhiteSpace(value.ActorServerId)) return "server:" + value.ActorServerId.Trim() + ":" + (value.ActorName ?? "").Trim();
            if (value.ActorServerRaw > 0) return "server-raw:" + value.ActorServerRaw + ":" + (value.ActorName ?? "").Trim();
            if (!String.IsNullOrWhiteSpace(value.ActorServer)) return "server-name:" + value.ActorServer.Trim() + ":" + (value.ActorName ?? "").Trim();
            return (value.ActorName ?? "").Trim();
        }

        private static string BuildRosterIdentity(CombatEvent value)
        {
            if (value == null) return "";
            if (!String.IsNullOrWhiteSpace(value.PlatformCharacterId)) return "platform:" + value.PlatformCharacterId.Trim();
            if (!String.IsNullOrWhiteSpace(value.ActorServerId)) return "server:" + value.ActorServerId.Trim() + ":" + (value.ActorName ?? "").Trim();
            if (value.ActorServerRaw > 0) return "server-raw:" + value.ActorServerRaw + ":" + (value.ActorName ?? "").Trim();
            if (!String.IsNullOrWhiteSpace(value.ActorServer)) return "server-name:" + value.ActorServer.Trim() + ":" + (value.ActorName ?? "").Trim();
            return "name:" + (value.ActorName ?? "").Trim();
        }

        private void ReplaceRosterKeyLocked(string previousKey, string newKey)
        {
            if (String.IsNullOrWhiteSpace(previousKey) || String.IsNullOrWhiteSpace(newKey) ||
                !_rosterParticipantKeys.Remove(previousKey)) return;
            _rosterParticipantKeys.Add(newKey);
        }

        private static bool IsConfirmedRosterEvidence(string evidence)
        {
            return String.Equals(evidence, "PACKET_SMALL_ROSTER_CONFIRMED", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(evidence, "PACKET_SOLO_ROSTER_CONFIRMED", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(evidence, "HUD_OCR_PARTY_ROSTER_CONFIRMED", StringComparison.OrdinalIgnoreCase);
        }

        private bool HasEncounterActivityLocked()
        {
            return _running || _startedAtUtc != DateTime.MinValue || _participants.Values.Any(row => row.TotalDamage > 0);
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
            if (String.IsNullOrWhiteSpace(value.ActorName) || !String.Equals(_self.CharacterName, value.ActorName, StringComparison.OrdinalIgnoreCase)) return false;
            if (!String.IsNullOrWhiteSpace(_self.ServerId) && !String.IsNullOrWhiteSpace(value.ActorServerId) &&
                !String.Equals(_self.ServerId, value.ActorServerId, StringComparison.OrdinalIgnoreCase)) return false;
            if (!String.IsNullOrWhiteSpace(_self.ServerName) && !String.IsNullOrWhiteSpace(value.ActorServer) &&
                !String.Equals(_self.ServerName, value.ActorServer, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
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
