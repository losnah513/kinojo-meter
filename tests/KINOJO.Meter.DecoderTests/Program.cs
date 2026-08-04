using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KinojoMeterPrototype
{
    internal static class DecoderTestProgram
    {
        public static int Main(string[] args)
        {
            if (args != null && args.Length == 2 && String.Equals(args[0], "--fixture", StringComparison.OrdinalIgnoreCase))
                return ReplayFixture(args[1]);
            var passed = DecoderSelfTest.Run() && RunCombatEngineTests();
            Console.WriteLine(passed ? "KINOJO decoder and combat-engine regression tests passed." : "KINOJO regression tests failed.");
            return passed ? 0 : 1;
        }

        private static bool RunCombatEngineTests()
        {
            var self = new CharacterProfile { CharacterKey = "self", CharacterName = "청소기", ServerName = "예레" };
            var engine = new CombatSessionEngine(self, 5);
            var names = new[] { "궁디뿡", "완투", "청소기", "쉰빵", "권트" };
            engine.ReplaceObservedParty(names.Select((name, index) => new CombatEvent
            {
                Kind = CombatEventKind.PartyMember,
                ActorId = "party:" + name,
                ActorName = name,
                PartyNumber = 1,
                PartySlot = index + 1
            }));
            var partial = names.Take(4).Select((name, index) => new CombatEvent
            {
                Kind = CombatEventKind.PartyMember,
                ActorId = "party:" + name,
                ActorName = name,
                PartyNumber = 1,
                PartySlot = index + 1
            }).ToList();
            engine.ReplaceObservedParty(partial);
            if (engine.Snapshot().Rows.Count(row => !row.IsEmpty) != 5) return EngineFailure("partial roster was removed before confirmation");
            engine.ReplaceObservedParty(partial);
            engine.ReplaceObservedParty(partial);
            if (engine.Snapshot().Rows.Count(row => !row.IsEmpty) != 4) return EngineFailure("partial roster was not removed after three confirmations");

            engine = new CombatSessionEngine(self, 5);
            engine.Apply(new CombatEvent { Kind = CombatEventKind.Damage, TimestampUtc = DateTime.UtcNow, ActorId = "a", ActorName = "청소기", TargetId = "boss", TargetRuntimeId = 1, TargetName = "1보스", Damage = 100, IsBoss = true });
            engine.Apply(new CombatEvent { Kind = CombatEventKind.Damage, TimestampUtc = DateTime.UtcNow.AddSeconds(1), ActorId = "b", ActorName = "권트", TargetId = "boss", TargetRuntimeId = 1, TargetName = "1보스", Damage = 300, IsBoss = true });
            var rows = engine.Snapshot().Rows.Where(row => !row.IsEmpty && row.TotalDamage > 0).ToList();
            var selfRow = rows.Single(row => row.Name == "청소기");
            var otherRow = rows.Single(row => row.Name == "권트");
            if (Math.Abs(selfRow.Share - 25.0) >= 0.001 || Math.Abs(otherRow.Share - 75.0) >= 0.001) return EngineFailure("damage shares are not party-total based");
            engine.Tick(DateTime.UtcNow.AddSeconds(20));
            var completed = engine.Snapshot();
            if (!completed.IsCleared || completed.CompletionMode != "DAMAGE_IDLE_12S") return EngineFailure("idle completion was not inferred");
            engine.Apply(new CombatEvent { Kind = CombatEventKind.Damage, TimestampUtc = DateTime.UtcNow.AddSeconds(21), ActorId = "a", ActorName = "청소기", TargetId = "boss", TargetRuntimeId = 1, TargetName = "1보스", Damage = 50, IsBoss = true });
            return engine.Snapshot().Rows.Where(row => !row.IsEmpty).Sum(row => row.TotalDamage) == 50 || EngineFailure("same runtime boss did not reset after inferred completion");
        }

        private static bool EngineFailure(string message)
        {
            Console.WriteLine("Combat engine regression failed: " + message);
            return false;
        }

        private static int ReplayFixture(string folder)
        {
            var framesPath = Path.Combine(folder ?? "", "frames.bin");
            var indexPath = Path.Combine(folder ?? "", "frames.tsv");
            if (!File.Exists(framesPath) || !File.Exists(indexPath)) return 2;
            var decoder = new AionBinaryFrameDecoder();
            var allEvents = new List<CombatEvent>();
            var rosterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            decoder.PartyRosterDetected += delegate(object sender, PartyRosterDetectedEventArgs value)
            {
                foreach (var member in value.Members ?? new List<DetectedPartyMember>())
                    if (!String.IsNullOrWhiteSpace(member.CharacterName)) rosterNames.Add(member.CharacterName);
            };
            var payload = File.ReadAllBytes(framesPath);
            foreach (var line in File.ReadLines(indexPath).Skip(1))
            {
                if (String.IsNullOrWhiteSpace(line) || line[0] == '#') continue;
                var cells = line.Split('\t');
                int length;
                long offset;
                if (cells.Length < 7 ||
                    !Int32.TryParse(cells[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out length) ||
                    !Int64.TryParse(cells[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) ||
                    offset < 0 || offset + length > payload.LongLength) continue;
                var bytes = new byte[length];
                Buffer.BlockCopy(payload, (int)offset, bytes, 0, length);
                var events = new List<CombatEvent>();
                decoder.TryDecode(new GameFrameEventArgs(
                    bytes,
                    DateTime.Parse(cells[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    cells[2] + "|" + cells[3],
                    cells[2],
                    cells[3]), events);
                allEvents.AddRange(events);
            }
            var identities = allEvents.Where(value => value.Kind == CombatEventKind.EntityIdentity)
                .Where(value => !String.IsNullOrWhiteSpace(value.ActorName))
                .GroupBy(value => value.ActorId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToDictionary(value => value.ActorId, value => value.ActorName, StringComparer.OrdinalIgnoreCase);
            var damage = allEvents.Where(value => value.Kind == CombatEventKind.Damage).ToList();
            var hp = allEvents.Where(value => value.Kind == CombatEventKind.BossHp).ToList();
            Console.WriteLine("fixture=" + Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            Console.WriteLine("roster=" + String.Join(",", rosterNames.OrderBy(value => value)));
            Console.WriteLine("identities=" + String.Join(",", identities.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)));
            foreach (var target in damage.GroupBy(value => value.TargetId).OrderBy(group => group.Key))
            {
                var targetHp = hp.Where(value => String.Equals(value.TargetId, target.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                var actors = target.GroupBy(value => value.ActorId).OrderBy(group => group.Key)
                    .Select(group => (identities.ContainsKey(group.Key) ? identities[group.Key] : group.Key) + ":" + group.Sum(value => value.Damage));
                Console.WriteLine("target=" + target.Key + " damage=" + target.Sum(value => value.Damage) +
                    " hits=" + target.Count() + " hpMaxObserved=" + (targetHp.Count == 0 ? 0 : targetHp.Max(value => value.MaxHp)) +
                    " hpLast=" + (targetHp.Count == 0 ? 0 : targetHp.Last().CurrentHp) +
                    " hpZeroCount=" + targetHp.Count(value => value.CurrentHp == 0) + " actors=" + String.Join(",", actors));
            }
            Console.WriteLine("events damage=" + damage.Count + " hp=" + hp.Count);
            return damage.Count > 0 && hp.Count > 0 ? 0 : 1;
        }
    }
}
