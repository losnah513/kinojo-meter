using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KinojoMeterPrototype
{
    internal static class DecoderSelfTest
    {
        public static bool Run()
        {
            try
            {
                var single = Hex("25 3c 37 d9 37 03 e8 a2 5a 47 36 32 23 47 00 12 b3 47 25 ba 28 42 00 00 01 3a 64 a9 00 e3 43 29 42 50 26 04 38 8c f9 02 16 00 d9 37 3a 64 a9 00 02 03 84 00 02 eb 25 2b 42 02 00 00 00 98 92 01 ff 90 2d 02 00 2f 1b 56 01 01 00 31 ee 0b 00 00 89 01 00 21 f4 cb 1f c1 36 01 00 00 00 00 00 01 4a 00 00 00 00 00 00 00 00 00 00 00 00 00 00 16 00 14 00 8d 8c f9 02 02 01 00 01 00 00 00 00 00 00 00 0e 00 36 1b 14 d9 bd 9f 01 00 00");
                var multi = Hex("26 04 38 8c f9 02 16 00 d9 37 44 c0 aa 00 0f 03 8c 00 02 9b 1a b3 42 02 00 00 00 80 9a 01 c1 95 08 02 00 26 04 38 8c f9 02 16 00 d9 37 44 c0 aa 00 0f 03 88 00 02 a5 1a b3 42 03 00 00 00 80 9a 01 88 e3 07 03 00 14 00 8d 8c f9 02 02 01 00 01 00 00 00 00 00 00 00 0e 00 36 d0 0c df bd 9f 01 00 00");
                var now = new DateTime(2026, 8, 2, 0, 2, 28, DateTimeKind.Utc);
                var decoder = new AionCombatDecoder();
                var events = new List<CombatEvent>();
                var namedSingle = Identity(7129, "청소기").Concat(Identity(48268, "훈련용허수아비")).Concat(single).ToArray();
                decoder.TryDecode(new GameFrameEventArgs(namedSingle, now, "flow", "connection", "B_TO_A"), events);
                var damage = events.Where(value => value.Kind == CombatEventKind.Damage).ToList();
                Require(damage.Count == 1 && damage[0].Damage == 739455 && damage[0].SkillId == 18712, "single hit");
                Require(damage[0].ActorName == "청소기" && damage[0].TargetName == "훈련용허수아비", "entity names");
                decoder.TryDecode(new GameFrameEventArgs(namedSingle, now.AddSeconds(1), "flow", "connection", "B_TO_A"), events);
                Require(events.Count(value => value.Kind == CombatEventKind.Damage) == 1, "tail duplicate suppression");

                decoder = new AionCombatDecoder();
                events.Clear();
                var split = multi.Length / 2;
                decoder.TryDecode(new GameFrameEventArgs(multi.Take(split).ToArray(), now, "flow", "split", "B_TO_A"), events);
                decoder.TryDecode(new GameFrameEventArgs(multi.Skip(split).ToArray(), now.AddMilliseconds(1), "flow", "split", "B_TO_A"), events);
                damage = events.Where(value => value.Kind == CombatEventKind.Damage).ToList();
                Require(damage.Count == 2, "multi hit count");
                Require(damage.Sum(value => value.Damage) == 261193, "multi hit total");
                Require(damage.All(value => value.SkillId == 19712), "multi hit skill");
                Require(damage.Select(value => value.HitSequence).Distinct().Count() == 2, "multi hit sequence");

                byte[] decoded;
                Require(AionCombatDecoder.TryDecompressLz4(new byte[] { 0x50, 1, 2, 3, 4, 5 }, 0, 6, 5, out decoded), "lz4 literal");
                Require(decoded.SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }), "lz4 literal result");

                decoder = new AionCombatDecoder();
                events.Clear();
                decoder.TryDecode(new GameFrameEventArgs(Lz4Envelope(single), now, "flow", "lz4", "B_TO_A"), events);
                Require(events.Count(value => value.Kind == CombatEventKind.Damage && value.Damage == 739455) == 1, "lz4 envelope damage");
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("DECODER_TEST", "Self test failed", ex);
                return false;
            }
        }

        private static void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Decoder self test failed: " + name);
        }

        private static byte[] Hex(string value)
        {
            return value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(part => Convert.ToByte(part, 16)).ToArray();
        }

        private static byte[] Identity(long entityId, string name)
        {
            var result = new List<byte> { 0x41, 0x36 };
            result.AddRange(VarUInt(entityId));
            var nameBytes = Encoding.UTF8.GetBytes(name);
            result.AddRange(new byte[] { 0x1C, 0x00, 0x01, (byte)nameBytes.Length });
            result.AddRange(nameBytes);
            return result.ToArray();
        }

        private static byte[] Lz4Envelope(byte[] value)
        {
            var compressed = new List<byte> { 0xF0 };
            var extension = value.Length - 15;
            while (extension >= 255) { compressed.Add(255); extension -= 255; }
            compressed.Add((byte)extension);
            compressed.AddRange(value);
            var length = 8 + compressed.Count;
            var result = new List<byte>
            {
                (byte)(length & 0xFF), (byte)((length >> 8) & 0xFF), 0xFF, 0xFF,
                (byte)(value.Length & 0xFF), (byte)((value.Length >> 8) & 0xFF),
                (byte)((value.Length >> 16) & 0xFF), (byte)((value.Length >> 24) & 0xFF)
            };
            result.AddRange(compressed);
            return result.ToArray();
        }

        private static IEnumerable<byte> VarUInt(long value)
        {
            do
            {
                var current = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) current |= 0x80;
                yield return current;
            } while (value != 0);
        }
    }
}
