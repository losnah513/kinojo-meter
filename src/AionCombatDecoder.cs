using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KinojoMeterPrototype
{
    // Observed AION2 contract (2026-08-04): length-prefixed * 04 38 damage records,
    // unsigned varints, and raw LZ4 blocks carried by the FF FF envelope.
    internal sealed class AionCombatDecoder
    {
        private sealed class ConnectionState
        {
            public sealed class TransportFingerprintState
            {
                public int RawUnpaired;
                public int Lz4Unpaired;
                public DateTime LastSeenUtc;
            }
            public string ScopeId;
            public readonly Dictionary<long, string> EntityNames = new Dictionary<long, string>();
            public readonly Dictionary<string, byte[]> RawTailByDirection = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, byte[]> EnvelopeStreamByDirection = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, DateTime> RecentHp = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            public readonly Dictionary<string, TransportFingerprintState> TransportFingerprints = new Dictionary<string, TransportFingerprintState>(StringComparer.Ordinal);
            public readonly Dictionary<long, long> ObservedMaxHp = new Dictionary<long, long>();
            public readonly HashSet<long> DamageTargets = new HashSet<long>();
            public readonly Dictionary<long, int> DamageCountByTarget = new Dictionary<long, int>();
            public readonly Dictionary<long, long> DamageTotalByTarget = new Dictionary<long, long>();
            public readonly Dictionary<long, long> LastEmittedHpByTarget = new Dictionary<long, long>();
            public readonly Dictionary<long, DateTime> LastHpAtByTarget = new Dictionary<long, DateTime>();
            public readonly Dictionary<long, int> DamageCountAtZeroByTarget = new Dictionary<long, int>();
        }

        private readonly Dictionary<string, ConnectionState> _states = new Dictionary<string, ConnectionState>(StringComparer.OrdinalIgnoreCase);
        private int _nextScopeId;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private const int RawTailSize = 4096;
        private const int MaximumEnvelopeStream = 10 * 1024 * 1024;

        public bool TryDecode(GameFrameEventArgs frame, IList<CombatEvent> events)
        {
            if (frame == null || frame.Frame == null || frame.Frame.Length == 0 || events == null) return false;
            ConnectionState state;
            if (!_states.TryGetValue(frame.ConnectionKey ?? "", out state))
            {
                state = new ConnectionState { ScopeId = "flow-" + (++_nextScopeId).ToString("D3") };
                _states[frame.ConnectionKey ?? ""] = state;
            }

            var direction = frame.Direction ?? "";
            byte[] tail;
            state.RawTailByDirection.TryGetValue(direction, out tail);
            var buffer = Concat(tail, frame.Frame);
            var oldTailLength = tail == null ? 0 : tail.Length;
            var found = DecodeBuffer(buffer, frame.TimestampUtc, state, events, oldTailLength, true, "RAW:" + direction);
            state.RawTailByDirection[direction] = SliceTail(buffer, RawTailSize);

            byte[] envelopeStream;
            state.EnvelopeStreamByDirection.TryGetValue(direction, out envelopeStream);
            var oldEnvelopeLength = envelopeStream == null ? 0 : envelopeStream.Length;
            envelopeStream = Concat(envelopeStream, frame.Frame);
            DecodeLz4EnvelopeStream(ref envelopeStream, frame.TimestampUtc, state, events, ref found, direction, oldEnvelopeLength);
            state.EnvelopeStreamByDirection[direction] = envelopeStream;
            CleanupRecent(state, frame.TimestampUtc);
            return found;
        }

        private static bool DecodeBuffer(byte[] buffer, DateTime timestampUtc, ConnectionState state, IList<CombatEvent> events, int minimumRecordEndOffset, bool ignoreLz4EnvelopeBytes, string sourceKind)
        {
            var found = false;
            var envelopeRanges = ignoreLz4EnvelopeBytes ? FindCompleteLz4EnvelopeRanges(buffer) : new List<Tuple<int, int>>();
            // Resolve names before damage. 0x3633 is the observed local-player identity,
            // while 0x3645 and 0x3641 carry other entity/party identities.
            for (var offset = 0; offset + 8 < buffer.Length; offset++)
            {
                if (IsInsideRange(offset, envelopeRanges)) continue;
                long entityId;
                string name;
                int identityEnd;
                if (!TryReadEntityIdentity(buffer, offset, out entityId, out name, out identityEnd) || identityEnd <= minimumRecordEndOffset) continue;
                string previous;
                if (state.EntityNames.TryGetValue(entityId, out previous) && String.Equals(previous, name, StringComparison.Ordinal)) continue;
                state.EntityNames[entityId] = name;
                events.Add(new CombatEvent
                {
                    Kind = CombatEventKind.EntityIdentity,
                    TimestampUtc = timestampUtc,
                    ActorId = EntityKey(state, entityId),
                    ActorRuntimeId = entityId,
                    ActorName = name
                });
                found = true;
            }

            for (var offset = 0; offset + 30 < buffer.Length; offset++)
            {
                if (IsInsideRange(offset, envelopeRanges) || buffer[offset + 1] != 0x04 || buffer[offset + 2] != 0x38) continue;
                var recordLength = buffer[offset] - 3;
                if (recordLength < 30 || recordLength > 96) continue;
                var recordEnd = offset + recordLength;
                if (recordEnd > buffer.Length || recordEnd <= minimumRecordEndOffset) continue;
                int cursor = offset + 3;
                long targetId;
                if (!TryReadVarUInt(buffer, ref cursor, recordEnd, out targetId) || targetId <= 0 || cursor + 2 > recordEnd) continue;
                if (!IsDamageEffectFlag(buffer[cursor], buffer[cursor + 1])) continue;
                cursor += 2;
                long actorId;
                if (!TryReadVarUInt(buffer, ref cursor, recordEnd, out actorId) || actorId <= 0 || cursor + 17 > recordEnd) continue;
                var actionId = ReadUInt32(buffer, cursor); cursor += 4;
                cursor += 5; // result metadata; individual effect bits are not yet validated.
                cursor += 4; // observed float/position field.
                var hitSequence = ReadUInt32(buffer, cursor); cursor += 4;
                long skillId;
                long damage;
                if (!TryReadVarUInt(buffer, ref cursor, recordEnd, out skillId) || !TryReadVarUInt(buffer, ref cursor, recordEnd, out damage)) continue;
                if (damage <= 0 || damage > 100000000000L) continue;
                var recordFingerprint = Fingerprint(buffer, offset, recordLength) + ":" + (sourceKind == null ? "" : sourceKind.Split(':')[1]);
                if (IsCrossTransportDuplicate(state, recordFingerprint, sourceKind, timestampUtc)) continue;

                state.DamageTargets.Add(targetId);
                int targetDamageCount;
                long targetDamageTotal;
                state.DamageCountByTarget.TryGetValue(targetId, out targetDamageCount);
                state.DamageTotalByTarget.TryGetValue(targetId, out targetDamageTotal);
                state.DamageCountByTarget[targetId] = targetDamageCount + 1;
                state.DamageTotalByTarget[targetId] = targetDamageTotal + damage;
                string actorName;
                string targetName;
                state.EntityNames.TryGetValue(actorId, out actorName);
                state.EntityNames.TryGetValue(targetId, out targetName);
                events.Add(new CombatEvent
                {
                    Kind = CombatEventKind.Damage,
                    TimestampUtc = timestampUtc,
                    ActorId = EntityKey(state, actorId),
                    ActorRuntimeId = actorId,
                    ActorName = actorName ?? "",
                    TargetId = EntityKey(state, targetId),
                    TargetRuntimeId = targetId,
                    TargetName = targetName ?? "",
                    Damage = damage,
                    ActionId = actionId,
                    SkillId = skillId,
                    HitSequence = hitSequence
                });
                found = true;
            }

            // Fixture 2026-08-02: 0x14 0x00 0x8D + target varint + 02 01 00
            // is followed by a monotonically decreasing 64-bit current HP value.
            // Restrict it to entities already observed as damage targets so player HP is not
            // promoted to a boss event.
            for (var offset = 0; offset + 18 <= buffer.Length; offset++)
            {
                if (IsInsideRange(offset, envelopeRanges) || buffer[offset] != 0x14 || buffer[offset + 1] != 0x00 || buffer[offset + 2] != 0x8D) continue;
                var recordEnd = offset + buffer[offset] - 3;
                if (recordEnd > buffer.Length || recordEnd <= minimumRecordEndOffset) continue;
                var cursor = offset + 3;
                long targetId;
                if (!TryReadVarUInt(buffer, ref cursor, recordEnd, out targetId) || targetId <= 0 || !state.DamageTargets.Contains(targetId)) continue;
                if (cursor + 11 > recordEnd || buffer[cursor] != 0x02 || buffer[cursor + 1] != 0x01 || buffer[cursor + 2] != 0x00) continue;
                cursor += 3;
                var currentHp = ReadInt64(buffer, cursor);
                if (currentHp < 0 || currentHp > 1000000000000L) continue;
                long observedMax;
                if (!state.ObservedMaxHp.TryGetValue(targetId, out observedMax) || currentHp > observedMax)
                {
                    observedMax = currentHp;
                    state.ObservedMaxHp[targetId] = observedMax;
                }
                int targetDamageCount;
                long targetDamageTotal;
                state.DamageCountByTarget.TryGetValue(targetId, out targetDamageCount);
                state.DamageTotalByTarget.TryGetValue(targetId, out targetDamageTotal);
                if (targetDamageCount < 10 || targetDamageTotal < 500000) continue;
                long lastEmittedHp;
                DateTime lastHpAt;
                if (state.LastEmittedHpByTarget.TryGetValue(targetId, out lastEmittedHp) && currentHp > lastEmittedHp)
                {
                    int countAtZero;
                    state.DamageCountAtZeroByTarget.TryGetValue(targetId, out countAtZero);
                    state.LastHpAtByTarget.TryGetValue(targetId, out lastHpAt);
                    var newEncounter = lastEmittedHp == 0 &&
                        timestampUtc - lastHpAt >= TimeSpan.FromSeconds(3) &&
                        targetDamageCount >= countAtZero + 5 &&
                        currentHp >= Math.Max(1L, observedMax / 2);
                    if (!newEncounter) continue;
                }
                var signature = targetId + ":" + currentHp;
                DateTime hpSeenAt;
                if (state.RecentHp.TryGetValue(signature, out hpSeenAt) && Math.Abs((timestampUtc - hpSeenAt).TotalSeconds) < 30) continue;
                state.RecentHp[signature] = timestampUtc;
                state.LastEmittedHpByTarget[targetId] = currentHp;
                state.LastHpAtByTarget[targetId] = timestampUtc;
                if (currentHp == 0) state.DamageCountAtZeroByTarget[targetId] = targetDamageCount;
                string targetName;
                state.EntityNames.TryGetValue(targetId, out targetName);
                events.Add(new CombatEvent
                {
                    Kind = CombatEventKind.BossHp,
                    TimestampUtc = timestampUtc,
                    TargetId = EntityKey(state, targetId),
                    TargetRuntimeId = targetId,
                    TargetName = targetName ?? "",
                    CurrentHp = currentHp,
                    MaxHp = observedMax,
                    BossHpSource = "OBSERVED_CURRENT_MAX",
                    IsBoss = false,
                    BossIdentityMode = "RUNTIME_HP_TARGET"
                });
                found = true;
            }
            return found;
        }

        private static void DecodeLz4EnvelopeStream(ref byte[] buffer, DateTime timestampUtc, ConnectionState state, IList<CombatEvent> events, ref bool found, string direction, int minimumEnvelopeEndOffset)
        {
            if (buffer == null || buffer.Length == 0) return;
            var incompleteOffsets = new List<int>();
            var lastCompleteEnd = -1;
            for (var offset = 0; offset + 9 <= buffer.Length; offset++)
            {
                if (buffer[offset + 2] != 0xFF || buffer[offset + 3] != 0xFF) continue;
                var declared = buffer[offset] | (buffer[offset + 1] << 8);
                var expected = (int)ReadUInt32(buffer, offset + 4);
                if (declared < 9 || expected <= 0 || expected > 8 * 1024 * 1024) continue;
                var envelopeLength = declared;
                if (offset + envelopeLength > buffer.Length)
                {
                    incompleteOffsets.Add(offset);
                    continue;
                }
                var compressedLength = envelopeLength - 8;
                byte[] decoded;
                if (!TryDecompressLz4(buffer, offset + 8, compressedLength, expected, out decoded))
                {
                    if (offset + envelopeLength + 2 > buffer.Length ||
                        !TryDecompressLz4(buffer, offset + 8, compressedLength + 2, expected, out decoded))
                        continue;
                    envelopeLength += 2;
                }
                if (offset + envelopeLength <= minimumEnvelopeEndOffset)
                {
                    lastCompleteEnd = Math.Max(lastCompleteEnd, offset + envelopeLength);
                    offset += envelopeLength - 1;
                    continue;
                }
                if (DecodeBuffer(decoded, timestampUtc, state, events, 0, false, "LZ4:" + (direction ?? ""))) found = true;
                lastCompleteEnd = Math.Max(lastCompleteEnd, offset + envelopeLength);
                offset += envelopeLength - 1;
            }

            var retainedOffset = incompleteOffsets.Where(value => value >= lastCompleteEnd).DefaultIfEmpty(-1).First();
            if (retainedOffset >= 0)
            {
                var remaining = new byte[buffer.Length - retainedOffset];
                Buffer.BlockCopy(buffer, retainedOffset, remaining, 0, remaining.Length);
                buffer = remaining;
            }
            else
                buffer = SliceTail(buffer, 8);
            if (buffer.Length > MaximumEnvelopeStream)
                buffer = SliceTail(buffer, MaximumEnvelopeStream);
        }

        internal static bool TryDecompressLz4(byte[] source, int start, int length, int expectedLength, out byte[] result)
        {
            result = null;
            if (source == null || start < 0 || length <= 0 || start + length > source.Length || expectedLength <= 0) return false;
            var output = new List<byte>(Math.Min(expectedLength, 1024 * 1024));
            var cursor = start;
            var end = start + length;
            try
            {
                while (cursor < end && output.Count < expectedLength)
                {
                    var token = source[cursor++];
                    var literalLength = token >> 4;
                    if (literalLength == 15)
                    {
                        byte extension;
                        do { if (cursor >= end) return false; extension = source[cursor++]; literalLength += extension; } while (extension == 255);
                    }
                    if (cursor + literalLength > end || output.Count + literalLength > expectedLength) return false;
                    for (var i = 0; i < literalLength; i++) output.Add(source[cursor++]);
                    if (cursor >= end || output.Count >= expectedLength) break;
                    if (cursor + 2 > end) return false;
                    var matchOffset = source[cursor] | (source[cursor + 1] << 8); cursor += 2;
                    if (matchOffset <= 0 || matchOffset > output.Count) return false;
                    var matchLength = token & 0x0F;
                    if (matchLength == 15)
                    {
                        byte extension;
                        do { if (cursor >= end) return false; extension = source[cursor++]; matchLength += extension; } while (extension == 255);
                    }
                    matchLength += 4;
                    if (output.Count + matchLength > expectedLength) return false;
                    for (var i = 0; i < matchLength; i++) output.Add(output[output.Count - matchOffset]);
                }
                if (output.Count != expectedLength) return false;
                result = output.ToArray();
                return true;
            }
            catch { return false; }
        }

        private static bool TryReadEntityIdentity(byte[] data, int offset, out long entityId, out string name, out int recordEnd)
        {
            entityId = 0;
            name = "";
            recordEnd = 0;
            if (data == null || offset < 0 || offset + 4 >= data.Length || data[offset + 1] != 0x36) return false;
            var marker = data[offset];
            if (marker != 0x33 && marker != 0x41 && marker != 0x45) return false;
            var cursor = offset + 2;
            if (!TryReadVarUInt(data, ref cursor, out entityId) || entityId <= 0) return false;

            var searchEnd = Math.Min(data.Length - 1, cursor + 14);
            for (var lengthOffset = cursor; lengthOffset <= searchEnd; lengthOffset++)
            {
                var byteLength = data[lengthOffset];
                if (byteLength < 2 || byteLength > 48 || lengthOffset + 1 + byteLength > data.Length) continue;
                string candidate;
                try { candidate = StrictUtf8.GetString(data, lengthOffset + 1, byteLength); }
                catch { continue; }
                if (!IsPlausibleName(candidate)) continue;
                name = candidate;
                recordEnd = lengthOffset + 1 + byteLength;
                return true;
            }
            return false;
        }

        private static bool TryReadVarUInt(byte[] data, ref int cursor, out long value)
        {
            return TryReadVarUInt(data, ref cursor, data == null ? 0 : data.Length, out value);
        }

        private static bool TryReadVarUInt(byte[] data, ref int cursor, int end, out long value)
        {
            value = 0;
            var shift = 0;
            while (data != null && cursor < data.Length && cursor < end && shift <= 63)
            {
                var current = data[cursor++];
                value |= ((long)(current & 0x7F)) << shift;
                if ((current & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        private static long ReadInt64(byte[] data, int offset)
        {
            ulong value = 0;
            for (var index = 0; index < 8; index++) value |= ((ulong)data[offset + index]) << (index * 8);
            return value > Int64.MaxValue ? -1 : (long)value;
        }

        private static string EntityKey(ConnectionState state, long value) { return (state == null ? "flow-000" : state.ScopeId) + ":entity:" + value; }
        private static bool IsDamageEffectFlag(byte first, byte second)
        {
            return (first == 0x06 || first == 0x16 || first == 0x26 || first == 0x36) &&
                (second == 0x00 || second == 0x04);
        }
        private static List<Tuple<int, int>> FindCompleteLz4EnvelopeRanges(byte[] buffer)
        {
            var ranges = new List<Tuple<int, int>>();
            if (buffer == null) return ranges;
            for (var offset = 0; offset + 9 <= buffer.Length; offset++)
            {
                if (buffer[offset + 2] != 0xFF || buffer[offset + 3] != 0xFF) continue;
                var declared = buffer[offset] | (buffer[offset + 1] << 8);
                var expected = (int)ReadUInt32(buffer, offset + 4);
                if (declared < 9 || expected <= 0 || expected > 8 * 1024 * 1024 || offset + declared > buffer.Length) continue;
                var envelopeLength = declared;
                byte[] decoded;
                if (!TryDecompressLz4(buffer, offset + 8, declared - 8, expected, out decoded))
                {
                    if (offset + declared + 2 > buffer.Length || !TryDecompressLz4(buffer, offset + 8, declared - 6, expected, out decoded)) continue;
                    envelopeLength += 2;
                }
                ranges.Add(Tuple.Create(offset, offset + envelopeLength));
                offset += envelopeLength - 1;
            }
            return ranges;
        }
        private static bool IsInsideRange(int offset, IList<Tuple<int, int>> ranges)
        {
            return ranges != null && ranges.Any(range => offset >= range.Item1 && offset < range.Item2);
        }
        private static bool IsCrossTransportDuplicate(ConnectionState state, string fingerprint, string sourceKind, DateTime timestampUtc)
        {
            if (state == null || String.IsNullOrWhiteSpace(fingerprint)) return false;
            ConnectionState.TransportFingerprintState observed;
            if (!state.TransportFingerprints.TryGetValue(fingerprint, out observed) || Math.Abs((timestampUtc - observed.LastSeenUtc).TotalSeconds) > 10)
            {
                observed = new ConnectionState.TransportFingerprintState();
                state.TransportFingerprints[fingerprint] = observed;
            }
            observed.LastSeenUtc = timestampUtc;
            var isLz4 = sourceKind != null && sourceKind.StartsWith("LZ4:", StringComparison.OrdinalIgnoreCase);
            if (isLz4 && observed.RawUnpaired > 0) { observed.RawUnpaired--; return true; }
            if (!isLz4 && observed.Lz4Unpaired > 0) { observed.Lz4Unpaired--; return true; }
            if (isLz4) observed.Lz4Unpaired++;
            else observed.RawUnpaired++;
            return false;
        }
        private static string Fingerprint(byte[] value, int offset, int count)
        {
            unchecked
            {
                ulong hash = 1469598103934665603UL;
                for (var index = 0; index < count; index++) { hash ^= value[offset + index]; hash *= 1099511628211UL; }
                return hash.ToString("X16");
            }
        }
        private static bool IsPlausibleName(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 16) return false;
            if (!value.All(character => !Char.IsControl(character) && (Char.IsLetterOrDigit(character) || character == '_' || character == '-'))) return false;
            var hasHangul = value.Any(character => character >= '\uAC00' && character <= '\uD7A3');
            var hasLetter = value.Any(Char.IsLetter);
            var hasDigit = value.Any(Char.IsDigit);
            // Two-byte ASCII fragments and mixed protocol counters were common false
            // identities in real fixtures. Korean two-character names remain valid.
            if (!hasHangul && value.Length < 3) return false;
            if (!hasHangul && hasLetter && hasDigit) return false;
            return true;
        }
        private static byte[] Concat(byte[] left, byte[] right)
        {
            if (left == null || left.Length == 0) return right.ToArray();
            var result = new byte[left.Length + right.Length];
            Buffer.BlockCopy(left, 0, result, 0, left.Length);
            Buffer.BlockCopy(right, 0, result, left.Length, right.Length);
            return result;
        }
        private static byte[] SliceTail(byte[] value, int count)
        {
            var length = Math.Min(value.Length, count);
            var result = new byte[length];
            Buffer.BlockCopy(value, value.Length - length, result, 0, length);
            return result;
        }
        private static void CleanupRecent(ConnectionState state, DateTime now)
        {
            foreach (var key in state.RecentHp.Where(pair => Math.Abs((now - pair.Value).TotalMinutes) > 2).Select(pair => pair.Key).ToList()) state.RecentHp.Remove(key);
            foreach (var key in state.TransportFingerprints.Where(pair => Math.Abs((now - pair.Value.LastSeenUtc).TotalSeconds) > 15).Select(pair => pair.Key).ToList()) state.TransportFingerprints.Remove(key);
        }
    }
}
