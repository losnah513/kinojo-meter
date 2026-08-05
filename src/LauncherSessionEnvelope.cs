using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace KinojoMeterPrototype
{
    internal static class LauncherSessionEnvelope
    {
        private const string Prefix = "KINOJO_LAUNCHER_SESSION_V1 ";

        public static bool TryRead(out LoginResult login, out string error)
        {
            login = null;
            error = "KINOJO Meter Launcher에서 실행해 주세요.";
            try
            {
                if (!Console.IsInputRedirected) return false;
                var line = Console.In.ReadLine();
                if (String.IsNullOrWhiteSpace(line) || !line.StartsWith(Prefix, StringComparison.Ordinal)) return false;
                var encoded = line.Substring(Prefix.Length).Trim();
                if (encoded.Length == 0 || encoded.Length > 2 * 1024 * 1024) return false;
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var serializer = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
                var envelope = serializer.DeserializeObject(raw) as Dictionary<string, object>;
                if (envelope == null || Number(envelope, "schemaVersion", 0) != 1) return false;

                DateTime issuedAt;
                if (!DateTime.TryParse(Text(envelope, "issuedAtUtc", ""), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out issuedAt) ||
                    Math.Abs((DateTime.UtcNow - issuedAt.ToUniversalTime()).TotalSeconds) > 30) return false;
                if (!String.Equals(Text(envelope, "coreVersion", ""), KinojoVersion.Current, StringComparison.Ordinal)) return false;
                Guid installationId;
                if (!Guid.TryParse(Text(envelope, "installationId", ""), out installationId)) return false;
                var sessionToken = Text(envelope, "sessionToken", "");
                if (sessionToken.Length < 20 || sessionToken.Length > 200) return false;

                var account = Dict(envelope, "account") ?? new Dictionary<string, object>();
                var characters = new List<CharacterProfile>();
                foreach (var row in DictList(envelope, "characters")) characters.Add(ParseCharacter(row));
                characters.RemoveAll(value => value == null || String.IsNullOrWhiteSpace(value.CharacterKey) || String.IsNullOrWhiteSpace(value.CharacterName));
                if (characters.Count == 0)
                {
                    error = "Launcher 인증 응답에 연결된 캐릭터가 없습니다.";
                    return false;
                }

                var roleLabel = Text(account, "roleLabel", Text(account, "role", "Member"));
                var roleLevel = Number(account, "roleLevel", RoleLevelFromLabel(roleLabel));
                login = new LoginResult
                {
                    SessionToken = sessionToken,
                    MainCharacterName = Text(account, "mainCharacterName", Text(account, "mainCharacter", "")),
                    RoleLabel = roleLabel,
                    RoleLevel = roleLevel,
                    IsMeterAdmin = Bool(account, "meterAdmin") || roleLevel >= 5,
                    DiagnosticsAllowed = Bool(account, "diagnosticsAllowed") || roleLevel >= 5,
                    IsPreview = false,
                    Characters = characters
                };
                error = "";
                return true;
            }
            catch
            {
                login = null;
                error = "Launcher 인증 정보를 확인하지 못했습니다. Launcher에서 다시 실행해 주세요.";
                return false;
            }
        }

        private static CharacterProfile ParseCharacter(Dictionary<string, object> row)
        {
            if (row == null) return null;
            return new CharacterProfile
            {
                CharacterKey = Text(row, "characterKey", Text(row, "character_key", "")),
                CharacterName = Text(row, "characterName", Text(row, "character_name", "")),
                MainCharacterName = Text(row, "mainCharacterName", Text(row, "main_character_name", "")),
                ServerId = Text(row, "serverId", Text(row, "server_id", "")),
                ServerName = Text(row, "serverName", Text(row, "server_name", "")),
                ClassKey = Text(row, "classKey", Text(row, "class_key", "")),
                ClassName = Text(row, "className", Text(row, "class_name", "")),
                CharKey = Text(row, "charKey", Text(row, "char_key", "")),
                ProfileImageUrl = Text(row, "profileImageUrl", Text(row, "profile_image_url", "")),
                DetailUrl = Text(row, "detailUrl", Text(row, "detail_url", "")),
                PveCombatPower = LongNumber(row, "pveCombatPower", LongNumber(row, "pve_combat_power", 0)),
                IsMain = Bool(row, "isMain") || Bool(row, "is_main")
            };
        }

        private static Dictionary<string, object> Dict(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static IEnumerable<Dictionary<string, object>> DictList(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value)) yield break;
            var values = value as IEnumerable;
            if (values == null) yield break;
            foreach (var item in values)
            {
                var row = item as Dictionary<string, object>;
                if (row != null) yield return row;
            }
        }

        private static string Text(Dictionary<string, object> source, string key, string fallback)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : fallback;
        }

        private static bool Bool(Dictionary<string, object> source, string key)
        {
            bool value;
            return Boolean.TryParse(Text(source, key, ""), out value) && value;
        }

        private static int Number(Dictionary<string, object> source, string key, int fallback)
        {
            int value;
            return Int32.TryParse(Text(source, key, ""), out value) ? value : fallback;
        }

        private static long LongNumber(Dictionary<string, object> source, string key, long fallback)
        {
            long value;
            return Int64.TryParse(Text(source, key, ""), out value) ? value : fallback;
        }

        private static int RoleLevelFromLabel(string role)
        {
            switch ((role ?? "").Trim().ToUpperInvariant())
            {
                case "MASTER": return 5;
                case "MANAGER": return 4;
                case "VIP": return 3;
                case "FAMILY": return 2;
                default: return 1;
            }
        }
    }
}
