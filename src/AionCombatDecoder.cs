using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KinojoMeterPrototype
{
    // Controlled fixture contract (2026-08-02): 0x26 0x04 0x38 damage records,
    // unsigned varints, and raw LZ4 blocks carried by the FF FF envelope.
    internal sealed class AionCombatDecoder
    {
        private sealed class ConnectionState
        {
            public readonly Dictionary<long, string> EntityNames = new Dictionary<long, string>();
            public readonly Dictionary<string, byte[]> TailByDirection = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, DateTime> RecentDamage = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        }

        private readonly Dictionary<string, ConnectionState> _states = new Dictionary<string, ConnectionState>(StringComparer.OrdinalIgnoreCase);
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private const int TailSize = 512;

        public bool TryDecode(GameFrameEventArgs frame, IList<CombatEvent> events)
        {
            if (frame == null || frame.Frame == null || frame.Frame.Length == 0 || events == null) return false;
            ConnectionState state;
            if (!_states.TryGetValue(frame.ConnectionKey ?? "", out state))
            {
                state = new ConnectionState();
                _states[frame.ConnectionKey ?? ""] = state;
            }

            byte[] tail;
            state.TailByDirection.TryGetValue(frame.Direction ?? "", out tail);
            var buffer = Concat(tail, frame.Frame);
            var found = DecodeBuffer(buffer, frame.TimestampUtc, state, events);
            DecodeLz4Envelopes(buffer, frame.TimestampUtc, state, events, ref found);
            state.TailByDirection[frame.Direction ?? ""] = SliceTail(buffer, TailSize);
            CleanupRecent(state, frame.TimestampUtc);
            return found;
        }

        private static bool DecodeBuffer(byte[] buffer, DateTime timestampUtc, ConnectionState state, IList<CombatEvent> events)
        {
            var found = false;
            // Resolve names before damage so a record earlier in the same decoded block is named immediately.
            for (var offset = 0; offset + 8 < buffer.Length; offset++)
            {
                if (buffer[offset] != 0x41 || buffer[offset + 1] != 0x36) continue;
                int cursor = offset + 2;
                long entityId;
                if (!TryReadVarUInt(buffer, ref cursor, out entityId) || entityId <= 0 || cursor + 4 > buffer.Length) continue;
                cursor++; // observed entity subtype
                if (buffer[cursor] != 0x00 || buffer[cursor + 1] != 0x01) continue;
                var nameLength = buffer[cursor + 2];
                cursor += 3;
                if (nameLength == 0 || nameLength > 48 || cursor + nameLength > buffer.Length) continue;
                string name;
                try { name = StrictUtf8.GetString(buffer, cursor, nameLength); }
                catch { continue; }
                if (!IsPlausibleName(name)) continue;
                string previous;
                if (state.EntityNames.TryGetValue(entityId, out previous) && String.Equals(previous, name, StringComparison.Ordinal)) continue;
                state.EntityNames[entityId] = name;
                events.Add(new CombatEvent
                {
                    Kind = CombatEventKind.EntityIdentity,
                    TimestampUtc = timestampUtc,
                    ActorId = EntityKey(entityId),
                    ActorName = name
                });
                found = true;
            }

            for (var offset = 0; offset + 30 < buffer.Length; offset++)
            {
                if (buffer[offset] != 0x26 || buffer[offset + 1] != 0x04 || buffer[offset + 2] != 0x38) continue;
                int cursor = offset + 3;
                long targetId;
                if (!TryReadVarUInt(buffer, ref cursor, out targetId) || targetId <= 0 || cursor + 2 > buffer.Length) continue;
                if (buffer[cursor] != 0x16 || buffer[cursor + 1] != 0x00) continue;
                cursor += 2;
                long actorId;
                if (!TryReadVarUInt(buffer, ref cursor, out actorId) || actorId <= 0 || cursor + 13 > buffer.Length) continue;
                var actionId = ReadUInt32(buffer, cursor); cursor += 4;
                cursor += 5; // result metadata; individual effect bits are not yet validated.
                cursor += 4; // observed float/position field.
                var hitSequence = ReadUInt32(buffer, cursor); cursor += 4;
                long skillId;
                long damage;
                if (!TryReadVarUInt(buffer, ref cursor, out skillId) || !TryReadVarUInt(buffer, ref cursor, out damage)) continue;
                if (damage <= 0 || damage > 100000000000L) continue;

                var signature = actorId + ":" + targetId + ":" + actionId + ":" + hitSequence + ":" + skillId + ":" + damage;
                DateTime seenAt;
                if (state.RecentDamage.TryGetValue(signature, out seenAt) && Math.Abs((timestampUtc - seenAt).TotalSeconds) < 30) continue;
                state.RecentDamage[signature] = timestampUtc;
                string actorName;
                string targetName;
                state.EntityNames.TryGetValue(actorId, out actorName);
                state.EntityNames.TryGetValue(targetId, out targetName);
                events.Add(new CombatEvent
                {
                    Kind = CombatEventKind.Damage,
                    TimestampUtc = timestampUtc,
                    ActorId = EntityKey(actorId),
                    ActorName = actorName ?? "",
                    TargetId = EntityKey(targetId),
                    TargetName = targetName ?? "",
                    Damage = damage,
                    ActionId = actionId,
                    SkillId = skillId,
                    HitSequence = hitSequence
                });
                found = true;
            }
            return found;
        }

        private static void DecodeLz4Envelopes(byte[] buffer, DateTime timestampUtc, ConnectionState state, IList<CombatEvent> events, ref bool found)
        {
            for (var offset = 0; offset + 9 <= buffer.Length; offset++)
            {
                if (buffer[offset + 2] != 0xFF || buffer[offset + 3] != 0xFF) continue;
                var declared = buffer[offset] | (buffer[offset + 1] << 8);
                var expected = (int)ReadUInt32(buffer, offset + 4);
                if (declared < 9 || expected <= 0 || expected > 8 * 1024 * 1024) continue;
                var envelopeLength = declared;
                if (offset + envelopeLength > buffer.Length && offset + envelopeLength + 2 <= buffer.Length) envelopeLength += 2;
                if (offset + envelopeLength > buffer.Length) continue;
                var compressedLength = envelopeLength - 8;
                byte[] decoded;
                if (!TryDecompressLz4(buffer, offset + 8, compressedLength, expected, out decoded)) continue;
                if (DecodeBuffer(decoded, timestampUtc, state, events)) found = true;
                offset += envelopeLength - 1;
            }
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

        private static bool TryReadVarUInt(byte[] data, ref int cursor, out long value)
        {
            value = 0;
            var shift = 0;
            while (cursor < data.Length && shift <= 63)
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

        private static string EntityKey(long value) { return "entity:" + value; }
        private static bool IsPlausibleName(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 16) return false;
            return value.All(character => !Char.IsControl(character) && (Char.IsLetterOrDigit(character) || character == '_' || character == '-'));
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
            foreach (var key in state.RecentDamage.Where(pair => Math.Abs((now - pair.Value).TotalMinutes) > 2).Select(pair => pair.Key).ToList()) state.RecentDamage.Remove(key);
        }
    }
}
