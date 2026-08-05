using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace KinojoMeterPrototype
{
    internal static class DecoderTestProgram
    {
        public static int Main(string[] args)
        {
            if (args != null && args.Length == 2 && String.Equals(args[0], "--fixture", StringComparison.OrdinalIgnoreCase))
                return ReplayFixture(args[1]);
            var passed = DecoderSelfTest.Run() && RunWindowTitleCharacterTests() && RunSmallPartyRosterTests() &&
                RunCombatEngineTests() && RunProfileRetryQueueTests();
            Console.WriteLine(passed ? "KINOJO decoder and combat-engine regression tests passed." : "KINOJO regression tests failed.");
            return passed ? 0 : 1;
        }

        private static bool RunWindowTitleCharacterTests()
        {
            var owned = new[] { "청소기", "꾸헹", "꾸힉" };
            if (AionWindowCharacterDetector.MatchOwnedCharacter("AION2 l 꾸힉", owned) != "꾸힉")
                return EngineFailure("AION2 window title did not identify the owned character");
            if (AionWindowCharacterDetector.MatchOwnedCharacter("AION2 | 꾸헹", owned) != "꾸헹")
                return EngineFailure("alternate AION2 title delimiter was not accepted");
            if (AionWindowCharacterDetector.MatchOwnedCharacter("PURPLE | 꾸힉", owned).Length != 0)
                return EngineFailure("non-AION window title was accepted");
            if (AionWindowCharacterDetector.MatchOwnedCharacter("AION2 l 다른사람", owned).Length != 0)
                return EngineFailure("unowned character was accepted");
            if (AionWindowCharacterDetector.MatchOwnedCharacter("AION2 l 꾸힉 꾸헹", owned).Length != 0)
                return EngineFailure("ambiguous owned-character title was accepted");
            return true;
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
            engine.ReplaceObservedParty(partial);
            if (engine.Snapshot().Rows.Count(row => !row.IsEmpty) != 5) return EngineFailure("bus passenger was removed by repeated partial rosters");

            var precombat = new CombatSessionEngine(self, 5);
            var three = new[] { "청소기", "따숩", "찜" }.Select((name, index) => new CombatEvent
            {
                Kind = CombatEventKind.PartyMember,
                ActorId = "party-probe:" + (1200 + index) + ":" + name,
                ActorName = name,
                ActorServerRaw = 1200 + index,
                PartyNumber = 1,
                PartySlot = index + 1
            }).ToList();
            precombat.ReplaceObservedParty(three, "PACKET_SMALL_ROSTER_CONFIRMED");
            precombat.ReplaceObservedParty(three.Take(2), "PACKET_SMALL_ROSTER_CONFIRMED");
            if (precombat.Snapshot().Rows.Count(row => !row.IsEmpty) != 2) return EngineFailure("confirmed pre-combat 3-to-2 leave did not converge");
            precombat.ReplaceObservedParty(three.Take(1), "PACKET_SOLO_ROSTER_CONFIRMED");
            if (precombat.Snapshot().Rows.Count(row => !row.IsEmpty) != 1) return EngineFailure("confirmed pre-combat 2-to-1 leave did not converge");

            var inCombat = new CombatSessionEngine(self, 5);
            inCombat.ReplaceObservedParty(three, "PACKET_SMALL_ROSTER_CONFIRMED");
            inCombat.Apply(new CombatEvent { Kind = CombatEventKind.Damage, TimestampUtc = DateTime.UtcNow, ActorId = three[0].ActorId, ActorName = three[0].ActorName, ActorServerRaw = three[0].ActorServerRaw, TargetId = "boss", Damage = 100, IsBoss = true });
            inCombat.ReplaceObservedParty(three.Take(2), "PACKET_SMALL_ROSTER_CONFIRMED");
            if (inCombat.Snapshot().Rows.Count(row => !row.IsEmpty) != 3) return EngineFailure("in-combat confirmed shrink removed an encounter participant");

            var sameName = new CombatSessionEngine(null, 5);
            sameName.ReplaceObservedParty(new[]
            {
                new CombatEvent { Kind = CombatEventKind.PartyMember, ActorId = "party-probe:1200:중복", ActorName = "중복", ActorServerRaw = 1200, PartyNumber = 1, PartySlot = 1 },
                new CombatEvent { Kind = CombatEventKind.PartyMember, ActorId = "party-probe:1201:중복", ActorName = "중복", ActorServerRaw = 1201, PartyNumber = 1, PartySlot = 2 }
            }, "PACKET_SMALL_ROSTER_CONFIRMED");
            if (sameName.Snapshot().Rows.Count(row => !row.IsEmpty) != 2) return EngineFailure("same-name characters on different servers were merged");
            sameName.ApplyProfile(new PartyProfileResult { ParticipantKey = "missing", CharacterName = "없는사람", ServerId = "999" });

            engine = new CombatSessionEngine(self, 5);
            engine.Apply(new CombatEvent { Kind = CombatEventKind.Damage, TimestampUtc = DateTime.UtcNow, ActorId = "a", ActorName = "청소기", TargetId = "boss", TargetRuntimeId = 1, TargetName = "1보스", BossOrder = 1, Damage = 100, IsBoss = true });
            engine.Apply(new CombatEvent { Kind = CombatEventKind.Damage, TimestampUtc = DateTime.UtcNow.AddSeconds(1), ActorId = "b", ActorName = "권트", TargetId = "boss", TargetRuntimeId = 1, TargetName = "1보스", BossOrder = 1, Damage = 300, IsBoss = true });
            var rows = engine.Snapshot().Rows.Where(row => !row.IsEmpty && row.TotalDamage > 0).ToList();
            var selfRow = rows.Single(row => row.Name == "청소기");
            var otherRow = rows.Single(row => row.Name == "권트");
            if (Math.Abs(selfRow.Share - 25.0) >= 0.001 || Math.Abs(otherRow.Share - 75.0) >= 0.001) return EngineFailure("damage shares are not party-total based");

            var hpEngine = new CombatSessionEngine(self, 5);
            hpEngine.Apply(new CombatEvent
            {
                Kind = CombatEventKind.BossHp,
                TimestampUtc = DateTime.UtcNow,
                TargetId = "boss-hp",
                TargetRuntimeId = 9,
                TargetName = "전투 대상",
                CurrentHp = 680000000,
                MaxHp = 680000000,
                BossHpSource = "OBSERVED_CURRENT_MAX",
                IsBoss = true
            });
            var hpSnapshot = hpEngine.Snapshot();
            if (hpSnapshot.BossHpSource != "OBSERVED_CURRENT_MAX" || hpSnapshot.BossCurrentHp != 680000000 || hpSnapshot.BossMaxHp != 680000000)
                return EngineFailure("boss HP provenance was not retained by the combat engine");

            engine.Tick(DateTime.UtcNow.AddSeconds(20));
            var phaseIdle = engine.Snapshot();
            if (phaseIdle.IsCleared || phaseIdle.IsRunning || phaseIdle.CompletionMode != "PHASE_IDLE_12S") return EngineFailure("idle gap was incorrectly finalized as a clear");

            var phaseTime = phaseIdle.LastEventUtc.AddSeconds(1);
            engine.Apply(new CombatEvent { Kind = CombatEventKind.Damage, TimestampUtc = phaseTime, ActorId = "a", ActorName = "청소기", TargetId = "boss-phase-2", TargetRuntimeId = 2, TargetName = "1보스", BossOrder = 1, Damage = 50, IsBoss = true });
            var resumed = engine.Snapshot();
            if (resumed.Rows.Where(row => !row.IsEmpty).Sum(row => row.TotalDamage) != 450) return EngineFailure("same boss order phase did not preserve accumulated damage");

            var completionRaised = false;
            engine.EncounterCompleted += delegate { completionRaised = true; };
            if (!engine.FinalizeCurrentEncounter("NEXT_BOSS_SIGNAL", phaseTime.AddSeconds(13))) return EngineFailure("explicit inferred completion was rejected");
            var completed = engine.Snapshot();
            if (!completionRaised || !completed.IsCleared || completed.CompletionMode != "NEXT_BOSS_SIGNAL") return EngineFailure("explicit inferred completion did not freeze the encounter");
            var frozenDps = completed.Rows.Where(row => !row.IsEmpty).Sum(row => row.Dps);
            var frozenEnd = completed.LastEventUtc;
            engine.ReplaceObservedParty(partial);
            engine.Apply(new CombatEvent { Kind = CombatEventKind.DungeonDetected, TimestampUtc = phaseTime.AddMinutes(5), DungeonKey = "hall", DungeonName = "환영의 회랑", DifficultyKey = "conquest", DifficultyName = "정복" });
            var enriched = engine.Snapshot();
            if (enriched.LastEventUtc != frozenEnd || enriched.Rows.Where(row => !row.IsEmpty).Sum(row => row.Dps) != frozenDps)
                return EngineFailure("completed snapshot timing changed during roster or metadata enrichment");
            if (enriched.DungeonName != "환영의 회랑" || enriched.DifficultyName != "정복") return EngineFailure("HUD metadata was not retained by the combat engine");
            return true;
        }

        private static bool RunSmallPartyRosterTests()
        {
            var detected = new List<PartyRosterDetectedEventArgs>();
            var candidateDetails = new List<string>();
            var decoder = new AionBinaryFrameDecoder(new[] { "청소기" });
            decoder.PartyRosterDetected += delegate(object sender, PartyRosterDetectedEventArgs value) { detected.Add(value); };
            decoder.PartyRosterCandidateObserved += delegate(object sender, string value) { candidateDetails.Add(value); };
            var threeMembers = BuildRosterFrame(new[] { "청소기", "따숩", "찜" });
            var frame = new GameFrameEventArgs(threeMembers, DateTime.UtcNow, "A>B", "A<>B", "A_TO_B");
            decoder.TryDecode(frame, new List<CombatEvent>());
            if (detected.Count != 0) return EngineFailure("small roster was accepted without repeated confirmation");
            decoder.TryDecode(new GameFrameEventArgs(new byte[] { 0x10, 0x20, 0x30, 0x40 }, DateTime.UtcNow, "A>B", "A<>B", "A_TO_B"), new List<CombatEvent>());
            if (detected.Count != 0) return EngineFailure("retained TCP tail was counted as an independent roster observation");
            decoder.TryDecode(frame, new List<CombatEvent>());
            if (detected.Count != 1 || detected[0].Members.Count != 3 ||
                !String.Equals(detected[0].Evidence, "PACKET_SMALL_ROSTER_CONFIRMED", StringComparison.Ordinal))
                return EngineFailure("confirmed 3/5 roster was not emitted · candidates=" + String.Join(" || ", candidateDetails));

            var solo = new GameFrameEventArgs(BuildRosterFrame(new[] { "청소기" }), DateTime.UtcNow, "A>B", "A<>B", "A_TO_B");
            decoder.TryDecode(solo, new List<CombatEvent>());
            decoder.TryDecode(solo, new List<CombatEvent>());
            if (detected.Count != 1) return EngineFailure("solo roster was accepted before three independent confirmations");
            decoder.TryDecode(solo, new List<CombatEvent>());
            if (detected.Count != 2 || detected[1].Members.Count != 1 ||
                !String.Equals(detected[1].Evidence, "PACKET_SOLO_ROSTER_CONFIRMED", StringComparison.Ordinal))
                return EngineFailure("confirmed solo roster was not emitted");

            var untrustedDetected = 0;
            var untrusted = new AionBinaryFrameDecoder(new[] { "다른캐릭터" });
            untrusted.PartyRosterDetected += delegate { untrustedDetected++; };
            untrusted.TryDecode(frame, new List<CombatEvent>());
            untrusted.TryDecode(frame, new List<CombatEvent>());
            if (untrustedDetected != 0) return EngineFailure("untrusted small roster bypassed the owned-character gate");

            var legacyDetected = 0;
            var legacy = new AionBinaryFrameDecoder();
            legacy.PartyRosterDetected += delegate { legacyDetected++; };
            var fourMembers = BuildRosterFrame(new[] { "청소기", "따숩", "찜", "네번째" });
            legacy.TryDecode(new GameFrameEventArgs(fourMembers, DateTime.UtcNow, "A>B", "C<>D", "A_TO_B"), new List<CombatEvent>());
            if (legacyDetected != 1) return EngineFailure("legacy 4+ roster recognition regressed");

            var duplicateNameDetected = new List<PartyRosterDetectedEventArgs>();
            var duplicateNameDecoder = new AionBinaryFrameDecoder(new[] { "중복" });
            duplicateNameDecoder.PartyRosterDetected += delegate(object sender, PartyRosterDetectedEventArgs value) { duplicateNameDetected.Add(value); };
            var duplicateNameFrame = new GameFrameEventArgs(BuildRosterFrame(new[] { "중복", "중복" }), DateTime.UtcNow, "A>B", "E<>F", "A_TO_B");
            duplicateNameDecoder.TryDecode(duplicateNameFrame, new List<CombatEvent>());
            duplicateNameDecoder.TryDecode(duplicateNameFrame, new List<CombatEvent>());
            if (duplicateNameDetected.Count != 1 || duplicateNameDetected[0].Members.Count != 2)
                return EngineFailure("same-name roster records from different raw servers were collapsed");
            return true;
        }

        private static bool RunProfileRetryQueueTests()
        {
            var now = DateTime.UtcNow;
            var row = new CombatRow { ParticipantKey = "party:one", Name = "따숩", ServerRaw = 1200 };
            var queue = new PartyProfileRetryQueue(TimeSpan.FromSeconds(25));
            string key;
            if (!queue.TryBegin(row, now, out key)) return EngineFailure("initial profile lookup was not scheduled");
            queue.Complete(key, row, false, now);
            if (queue.TakeDue(now.AddSeconds(24), new[] { row }).Count != 0) return EngineFailure("profile retry ran before the 25-second interval");
            var due = queue.TakeDue(now.AddSeconds(25), new[] { row });
            if (due.Count != 1 || !queue.TryBegin(due[0], now.AddSeconds(25), out key)) return EngineFailure("unresolved profile was not retried automatically");
            queue.Complete(key, row, true, now.AddSeconds(25));
            if (queue.TakeDue(now.AddMinutes(1), new[] { row }).Count != 0) return EngineFailure("resolved profile remained in the retry queue");

            if (!queue.TryBegin(row, now.AddMinutes(2), out key)) return EngineFailure("new profile lookup was not scheduled after resolution");
            queue.Complete(key, row, false, now.AddMinutes(2));
            if (queue.TakeDue(now.AddMinutes(3), new CombatRow[0]).Count != 0) return EngineFailure("departed participant remained in the retry queue");
            return true;
        }

        private static byte[] BuildRosterFrame(IEnumerable<string> names)
        {
            var bytes = new List<byte> { 0x41, 0x36, 0x01 };
            var serverRaw = 1200;
            var classRaw = 1;
            foreach (var name in names)
            {
                Write7Bit(bytes, serverRaw++);
                var encoded = Encoding.UTF8.GetBytes(name);
                bytes.Add((byte)encoded.Length);
                bytes.AddRange(encoded);
                bytes.AddRange(BitConverter.GetBytes(classRaw++));
                bytes.AddRange(BitConverter.GetBytes(50));
                bytes.AddRange(new byte[4]);
            }
            return bytes.ToArray();
        }

        private static void Write7Bit(ICollection<byte> destination, int value)
        {
            do
            {
                var current = value & 0x7F;
                value >>= 7;
                if (value > 0) current |= 0x80;
                destination.Add((byte)current);
            }
            while (value > 0);
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
            var reassembly = new TcpReassemblyService();
            var allEvents = new List<CombatEvent>();
            var rosterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            decoder.PartyRosterDetected += delegate(object sender, PartyRosterDetectedEventArgs value)
            {
                foreach (var member in value.Members ?? new List<DetectedPartyMember>())
                    if (!String.IsNullOrWhiteSpace(member.CharacterName)) rosterNames.Add(member.CharacterName);
            };
            reassembly.StreamData += delegate(object sender, GameFrameEventArgs frame)
            {
                var events = new List<CombatEvent>();
                decoder.TryDecode(frame, events);
                allEvents.AddRange(events);
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
                uint sequence;
                if (!UInt32.TryParse(cells[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence)) continue;
                var connection = cells[2];
                var source = String.Equals(cells[3], "A_TO_B", StringComparison.OrdinalIgnoreCase) ? "A|" + connection : "B|" + connection;
                var destination = String.Equals(cells[3], "A_TO_B", StringComparison.OrdinalIgnoreCase) ? "B|" + connection : "A|" + connection;
                reassembly.Push(new CapturedTcpPayloadEventArgs(
                    bytes,
                    DateTime.Parse(cells[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    source,
                    destination,
                    sequence));
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
