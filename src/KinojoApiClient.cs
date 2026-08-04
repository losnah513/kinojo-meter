using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KinojoMeterPrototype
{
    internal sealed class KinojoApiClient
    {
        internal static string ClientVersion { get { return KinojoVersion.Current; } }
        private const string SiteConfigUrl = "https://kinojo.info/config.json";
        private readonly HttpClient _http;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private string _supabaseUrl;
        private string _publishableKey;

        public KinojoApiClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(20);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "KINOJO-Meter/" + ClientVersion);
        }

        public async Task<MeterUpdateCheckResult> GetDesktopUpdateAsync()
        {
            await EnsureConfigAsync();
            var result = await PostAsync(new Dictionary<string, object>
            {
                { "action", "desktopUpdate" },
                { "channel", KinojoVersion.Channel },
                { "clientVersion", ClientVersion }
            });
            if (!Bool(result, "ok"))
                throw new MeterApiException("UPDATE_MANIFEST_REJECTED", Text(result, "message", "업데이트 정보를 확인하지 못했습니다."));

            return new MeterUpdateCheckResult
            {
                ReleaseAvailable = Bool(result, "releaseAvailable"),
                UpdateAvailable = Bool(result, "updateAvailable"),
                ClientVersionValid = !result.ContainsKey("clientVersionValid") || Bool(result, "clientVersionValid"),
                Channel = Text(result, "channel", KinojoVersion.Channel),
                DesktopUpdate = ParseUpdate(Dict(result, "desktopUpdate"))
            };
        }

        public async Task<MeterCatalog> DesktopBootstrapAsync(string clientCatalogVersion = null)
        {
            await EnsureConfigAsync();
            var result = await PostAsync(new Dictionary<string, object>
            {
                { "action", "desktopBootstrap" },
                { "catalogVersion", clientCatalogVersion ?? "" },
                { "channel", KinojoVersion.Channel },
                { "clientVersion", ClientVersion }
            });
            if (!Bool(result, "ok"))
                throw new MeterApiException("BOOTSTRAP_REJECTED", Text(result, "message", "Server Catalog bootstrap에 실패했습니다."));

            var catalogNode = Dict(result, "catalog");
            if (catalogNode == null)
                throw new MeterApiException("CATALOG_MISSING", "Server bootstrap 응답에 Catalog가 없습니다.");
            if (!Bool(catalogNode, "ok"))
                throw new MeterApiException("CATALOG_REJECTED", Text(catalogNode, "message", "Server Catalog를 불러오지 못했습니다."));

            var catalog = ParseCatalog(catalogNode);
            catalog.DatabaseContract = Text(result, "databaseContract", "");
            catalog.DesktopUpdate = ParseUpdate(Dict(result, "desktopUpdate") ?? Dict(result, "update") ?? Dict(catalogNode, "desktopUpdate") ?? Dict(catalogNode, "update"));
            if (String.IsNullOrWhiteSpace(catalog.CatalogVersion))
                throw new MeterApiException("CATALOG_VERSION_MISSING", "Server Catalog 버전을 확인하지 못했습니다.");
            if (catalog.Contents.Count == 0 || catalog.Dungeons.Count == 0 || catalog.Variants.Count == 0)
                throw new MeterApiException("CATALOG_EMPTY", "Server Catalog의 콘텐츠·던전·난이도 정보가 비어 있습니다.");
            return catalog;
        }

        public async Task<LoginResult> LoginAsync(string passKey)
        {
            await EnsureConfigAsync();
            var payload = new Dictionary<string, object>
            {
                { "action", "login" },
                { "passKey", passKey },
                { "clientVersion", ClientVersion }
            };
            var result = await PostAsync(payload);
            if (!Bool(result, "ok"))
                throw new MeterApiException("LOGIN_REJECTED", Text(result, "message", "PASS KEY 인증에 실패했습니다."));

            var login = new LoginResult
            {
                SessionToken = Text(result, "sessionToken", ""),
                MainCharacterName = "",
                RoleLabel = "Member",
                RoleLevel = 1,
                IsMeterAdmin = false,
                DiagnosticsAllowed = false,
                IsPreview = false,
                Characters = new List<CharacterProfile>()
            };

            var account = Dict(result, "account");
            if (account != null)
            {
                login.MainCharacterName = Text(account, "mainCharacterName", Text(account, "mainCharacter", ""));
                login.RoleLabel = Text(account, "roleLabel", Text(account, "role", "Member"));
                login.RoleLevel = IntNumber(account, "roleLevel", RoleLevelFromLabel(login.RoleLabel));
                login.IsMeterAdmin = Bool(account, "meterAdmin") || login.RoleLevel >= 5;
                login.DiagnosticsAllowed = Bool(account, "diagnosticsAllowed") || login.IsMeterAdmin;
            }

            foreach (var row in DictList(result, "characters"))
                login.Characters.Add(ParseCharacter(row));

            if (login.Characters.Count == 0)
                throw new MeterApiException("CHARACTER_NOT_LINKED", "계정에 연결된 활성 캐릭터를 찾지 못했습니다. member_codes의 본캐명과 character_master 연결을 확인해 주세요.");
            if (String.IsNullOrWhiteSpace(login.SessionToken))
                throw new MeterApiException("SESSION_MISSING", "서버가 로그인 세션을 반환하지 않았습니다.");
            return login;
        }

        public async Task SelectCharacterAsync(string sessionToken, CharacterProfile character)
        {
            await EnsureConfigAsync();
            var result = await PostAsync(new Dictionary<string, object>
            {
                { "action", "selectCharacter" },
                { "sessionToken", sessionToken },
                { "characterKey", character.CharacterKey }
            });
            if (!Bool(result, "ok"))
                throw new MeterApiException("CHARACTER_SELECT_REJECTED", Text(result, "message", "캐릭터 선택을 확인하지 못했습니다."));

            var selected = Dict(result, "selectedCharacter");
            if (selected != null)
            {
                character.ClassKey = Text(selected, "classKey", character.ClassKey ?? "");
                character.ClassName = Text(selected, "className", character.ClassName ?? "");
                character.ServerId = Text(selected, "serverId", character.ServerId ?? "");
                character.ServerName = Text(selected, "serverName", character.ServerName ?? "");
                character.PveCombatPower = Number(selected, "pveCombatPower", character.PveCombatPower);
            }
        }

        public async Task<CanonicalCatalogSelection> ResolveEncounterCatalogAsync(
            EncounterCatalogContext context,
            CharacterProfile character,
            string detectedBossName)
        {
            if (context == null) throw new MeterApiException("ENCOUNTER_CONTEXT_MISSING", "전투 콘텐츠 기준정보가 선택되지 않았습니다.");
            if (character == null) throw new MeterApiException("CHARACTER_MISSING", "선택 캐릭터 정보가 없습니다.");
            if (String.IsNullOrWhiteSpace(detectedBossName)) throw new MeterApiException("BOSS_NAME_MISSING", "전투에서 확인된 보스명이 없습니다.");

            await EnsureConfigAsync();
            var payload = new Dictionary<string, object>
            {
                { "classKey", character.ClassKey ?? "" },
                { "className", character.ClassName ?? "" },
                { "contentKey", context.ContentKey ?? "" },
                { "contentName", context.ContentName ?? "" },
                { "dungeonKey", context.DungeonKey ?? "" },
                { "dungeonName", context.DungeonName ?? "" },
                { "difficultyKey", context.DifficultyKey ?? "" },
                { "difficultyName", context.DifficultyName ?? "" },
                { "variantKey", context.VariantKey ?? "" },
                { "bossName", detectedBossName.Trim() }
            };
            var result = await PostAsync(new Dictionary<string, object>
            {
                { "action", "resolveEncounterCatalog" },
                { "payload", payload }
            });

            var selection = new CanonicalCatalogSelection
            {
                Ok = Bool(result, "ok"),
                Message = Text(result, "message", ""),
                ReasonCode = Text(result, "reasonCode", ""),
                CatalogVersion = Text(result, "catalogVersion", ""),
                ClassKey = Text(result, "classKey", ""),
                ContentKey = Text(result, "contentKey", ""),
                ContentName = Text(result, "contentName", ""),
                DungeonKey = Text(result, "dungeonKey", ""),
                DungeonName = Text(result, "dungeonName", ""),
                DifficultyKey = Text(result, "difficultyKey", ""),
                DifficultyName = Text(result, "difficultyName", ""),
                VariantKey = Text(result, "variantKey", ""),
                BossKey = Text(result, "bossKey", ""),
                BossName = Text(result, "bossName", "")
            };
            if (!selection.Ok)
                throw new MeterApiException(
                    String.IsNullOrWhiteSpace(selection.ReasonCode) ? "CATALOG_RESOLUTION_REJECTED" : selection.ReasonCode,
                    String.IsNullOrWhiteSpace(selection.Message) ? "전투 기준정보를 Server Catalog에서 확정하지 못했습니다." : selection.Message);
            if (String.IsNullOrWhiteSpace(selection.CatalogVersion) ||
                String.IsNullOrWhiteSpace(selection.ClassKey) ||
                String.IsNullOrWhiteSpace(selection.ContentKey) ||
                String.IsNullOrWhiteSpace(selection.DungeonKey) ||
                String.IsNullOrWhiteSpace(selection.DifficultyKey) ||
                String.IsNullOrWhiteSpace(selection.VariantKey) ||
                String.IsNullOrWhiteSpace(selection.BossKey))
                throw new MeterApiException("CANONICAL_RESULT_INCOMPLETE", "Server가 반환한 canonical key가 완전하지 않습니다.");
            return selection;
        }

        public async Task<List<PartyProfileResult>> GetPartyProfilesAsync(string sessionToken, IEnumerable<CombatRow> participants)
        {
            if (String.IsNullOrWhiteSpace(sessionToken)) throw new MeterApiException("SESSION_MISSING", "파티원 프로필 조회를 위한 서버 세션이 없습니다.");
            await EnsureConfigAsync();
            var rows = new List<object>();
            foreach (var row in participants ?? Enumerable.Empty<CombatRow>())
            {
                if (row == null || row.IsEmpty || String.IsNullOrWhiteSpace(row.Name)) continue;
                rows.Add(new Dictionary<string, object>
                {
                    { "participantKey", row.ParticipantKey ?? "" },
                    { "platformCharacterId", row.PlatformCharacterId ?? "" },
                    { "serverId", row.ServerId ?? "" },
                    { "serverName", row.ServerName ?? "" },
                    { "characterName", row.Name ?? "" },
                    { "classKey", row.ClassKey ?? "" },
                    { "className", row.ClassName ?? "" },
                    { "profileImageUrl", row.ProfileImageUrl ?? "" },
                    { "pveCombatPower", row.CombatPower },
                    { "itemLevel", row.ItemLevel },
                    { "partyNumber", row.PartyNumber },
                    { "partySlot", row.PartySlot },
                    { "isSelf", row.IsSelf }
                });
            }
            if (rows.Count == 0) return new List<PartyProfileResult>();
            var result = await PostAsync(new Dictionary<string, object>
            {
                { "action", "partyProfiles" },
                { "sessionToken", sessionToken },
                { "participants", rows }
            });
            if (!Bool(result, "ok")) throw new MeterApiException("PARTY_PROFILE_REJECTED", Text(result, "message", "파티원 공개 프로필을 불러오지 못했습니다."));
            return DictList(result, "profiles").Select(row => new PartyProfileResult
            {
                Ok = Bool(row, "ok"),
                ReasonCode = Text(row, "reasonCode", ""),
                Message = Text(row, "message", ""),
                ParticipantKey = Text(row, "participantKey", ""),
                MeterCharacterId = Number(row, "meterCharacterId", 0),
                PlatformCharacterId = Text(row, "platformCharacterId", ""),
                ServerId = Text(row, "serverId", ""),
                ServerName = Text(row, "serverName", ""),
                CharacterName = Text(row, "characterName", ""),
                ClassKey = Text(row, "classKey", ""),
                ClassName = Text(row, "className", ""),
                ProfileImageUrl = Text(row, "profileImageUrl", ""),
                PveCombatPower = Number(row, "pveCombatPower", 0),
                PvpCombatPower = Number(row, "pvpCombatPower", 0),
                ItemLevel = Number(row, "itemLevel", 0),
                ProfileStatus = Text(row, "profileStatus", ""),
                ProfileRefreshStatus = Text(row, "profileRefreshStatus", "")
            }).ToList();
        }

        public async Task LogoutAsync(string sessionToken)
        {
            if (String.IsNullOrWhiteSpace(sessionToken)) return;
            try
            {
                await EnsureConfigAsync();
                await PostAsync(new Dictionary<string, object>
                {
                    { "action", "logout" },
                    { "sessionToken", sessionToken }
                });
            }
            catch
            {
                // 앱 종료 시 서버 세션 회수 실패는 사용자 종료를 막지 않습니다.
            }
        }

        public async Task<Dictionary<string, object>> SubmitEncounterAsync(string sessionToken, Dictionary<string, object> payload)
        {
            if (String.IsNullOrWhiteSpace(sessionToken)) throw new MeterApiException("SESSION_MISSING", "전투 저장을 위한 서버 세션이 없습니다.");
            if (payload == null) throw new MeterApiException("ENCOUNTER_EMPTY", "전투 데이터가 비어 있습니다.");
            await EnsureConfigAsync();
            var result = await PostAsync(new Dictionary<string, object>
            {
                { "action", "submitEncounter" },
                { "sessionToken", sessionToken },
                { "payload", payload }
            });
            if (!Bool(result, "ok"))
                throw new MeterApiException("ENCOUNTER_REJECTED", Text(result, "message", "전투 저장이 거부되었습니다."));
            return result;
        }

        public LoginResult CreatePreview(string typedPassKey)
        {
            if (String.IsNullOrWhiteSpace(typedPassKey))
                throw new MeterApiException("PASS_KEY_EMPTY", "미리보기에서도 PASS KEY 입력 흐름을 확인할 수 있도록 값을 입력해 주세요.");

            return new LoginResult
            {
                SessionToken = "",
                MainCharacterName = "키노조",
                RoleLabel = "PROTOTYPE",
                IsPreview = true,
                Characters = new List<CharacterProfile>
                {
                    new CharacterProfile { CharacterKey="preview:2002:kinojo", CharacterName="키노조", MainCharacterName="키노조", ServerId="2002", ServerName="지켈", ClassKey="RANGER", ClassName="궁성", PveCombatPower=485000, IsMain=true },
                    new CharacterProfile { CharacterKey="preview:2002:healer", CharacterName="키노조힐", MainCharacterName="키노조", ServerId="2002", ServerName="지켈", ClassKey="CLERIC", ClassName="치유성", PveCombatPower=421000, IsMain=false },
                    new CharacterProfile { CharacterKey="preview:2002:tank", CharacterName="키노조탱", MainCharacterName="키노조", ServerId="2002", ServerName="지켈", ClassKey="TEMPLAR", ClassName="수호성", PveCombatPower=398000, IsMain=false }
                }
            };
        }

        private async Task EnsureConfigAsync()
        {
            if (!String.IsNullOrWhiteSpace(_supabaseUrl) && !String.IsNullOrWhiteSpace(_publishableKey)) return;
            string configText;
            try
            {
                configText = await _http.GetStringAsync(SiteConfigUrl + "?meter=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            catch (TaskCanceledException ex)
            {
                throw new MeterApiException("CONFIG_TIMEOUT", "KINOJO 설정 서버 응답 시간이 초과되었습니다.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new MeterApiException("CONFIG_NETWORK", "KINOJO 설정 서버에 연결하지 못했습니다. 인터넷 연결과 kinojo.info 접속을 확인해 주세요.", ex);
            }

            Dictionary<string, object> config;
            try { config = _json.DeserializeObject(configText) as Dictionary<string, object>; }
            catch (Exception ex) { throw new MeterApiException("CONFIG_FORMAT", "KINOJO config.json 형식이 올바르지 않습니다.", ex); }
            if (config == null) throw new MeterApiException("CONFIG_FORMAT", "KINOJO config.json 최상위 형식을 읽지 못했습니다.");
            var supabase = Dict(config, "supabase");
            if (supabase == null) throw new MeterApiException("CONFIG_SUPABASE_MISSING", "config.json에서 Supabase 설정을 찾지 못했습니다.");
            _supabaseUrl = Text(supabase, "url", "").TrimEnd('/');
            _publishableKey = Text(supabase, "publishableKey", "");
            if (String.IsNullOrWhiteSpace(_supabaseUrl) || String.IsNullOrWhiteSpace(_publishableKey))
                throw new MeterApiException("CONFIG_EMPTY", "KINOJO 서버 연결 설정이 비어 있습니다.");
        }

        private async Task<Dictionary<string, object>> PostAsync(Dictionary<string, object> payload)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _supabaseUrl + "/functions/v1/meter-ingest");
            request.Headers.TryAddWithoutValidation("apikey", _publishableKey);
            request.Content = new StringContent(_json.Serialize(payload), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try { response = await _http.SendAsync(request); }
            catch (TaskCanceledException ex) { throw new MeterApiException("EDGE_TIMEOUT", "미터기 인증 서버 응답 시간이 초과되었습니다.", ex); }
            catch (HttpRequestException ex) { throw new MeterApiException("EDGE_NETWORK", "미터기 인증 서버에 요청을 보내지 못했습니다.", ex); }

            var raw = await response.Content.ReadAsStringAsync();
            Dictionary<string, object> result;
            try { result = _json.DeserializeObject(raw) as Dictionary<string, object>; }
            catch { result = new Dictionary<string, object> { { "ok", false }, { "message", raw } }; }
            if (result == null) result = new Dictionary<string, object> { { "ok", false }, { "message", "서버 JSON 최상위 형식을 읽지 못했습니다." } };

            if (!response.IsSuccessStatusCode)
            {
                IEnumerable<string> values;
                var edgeCode = response.Headers.TryGetValues("sb-error-code", out values) ? String.Join(",", values) : "";
                var message = Text(result, "message", Text(result, "error", "서버 HTTP " + (int)response.StatusCode));
                if ((int)response.StatusCode == 401)
                    message = "Edge Function 외부 호출 인증이 거부되었습니다. meter-ingest의 verify_jwt=false 설정을 확인해 주세요.";
                throw new MeterApiException(String.IsNullOrWhiteSpace(edgeCode) ? "EDGE_HTTP_" + (int)response.StatusCode : edgeCode, message);
            }
            return result;
        }

        private static MeterCatalog ParseCatalog(Dictionary<string, object> node)
        {
            var catalog = new MeterCatalog
            {
                CatalogVersion = Text(node, "catalogVersion", "")
            };

            var contentRows = DictList(node, "contentTypes").ToList();
            if (contentRows.Count == 0) contentRows = DictList(node, "contents").ToList();
            foreach (var row in contentRows)
            {
                catalog.Contents.Add(new CatalogContent
                {
                    ContentKey = Text(row, "contentKey", ""),
                    DisplayName = Text(row, "displayName", Text(row, "shortName", "")),
                    ShortName = Text(row, "shortName", ""),
                    PartySize = IntNumber(row, "partySize", 5),
                    DisplayOrder = IntNumber(row, "displayOrder", 0)
                });
            }
            foreach (var row in DictList(node, "dungeons"))
            {
                catalog.Dungeons.Add(new CatalogDungeon
                {
                    ContentKey = Text(row, "contentKey", ""),
                    DungeonKey = Text(row, "dungeonKey", ""),
                    DungeonName = Text(row, "dungeonName", ""),
                    Tier = IntNumber(row, "tier", 0),
                    OrderInTier = IntNumber(row, "orderInTier", 0),
                    PartySize = IntNumber(row, "partySize", 5)
                });
            }
            foreach (var row in DictList(node, "difficulties"))
            {
                catalog.Difficulties.Add(new CatalogDifficulty
                {
                    ContentKey = Text(row, "contentKey", ""),
                    DifficultyKey = Text(row, "difficultyKey", ""),
                    DisplayName = Text(row, "displayName", ""),
                    DisplayOrder = IntNumber(row, "displayOrder", 0)
                });
            }
            foreach (var row in DictList(node, "variants"))
            {
                catalog.Variants.Add(new CatalogVariant
                {
                    ContentKey = Text(row, "contentKey", ""),
                    DungeonKey = Text(row, "dungeonKey", ""),
                    DifficultyKey = Text(row, "difficultyKey", ""),
                    VariantKey = Text(row, "variantKey", ""),
                    Tier = IntNumber(row, "tier", 0)
                });
            }
            foreach (var row in DictList(node, "bosses"))
            {
                catalog.Bosses.Add(new CatalogBoss
                {
                    DungeonKey = Text(row, "dungeonKey", ""),
                    BossKey = Text(row, "bossKey", ""),
                    BossName = Text(row, "bossName", ""),
                    BossOrder = IntNumber(row, "bossOrder", 0)
                });
            }
            foreach (var row in DictList(node, "variantBosses"))
            {
                catalog.VariantBosses.Add(new CatalogVariantBoss
                {
                    VariantKey = Text(row, "variantKey", ""),
                    BossKey = Text(row, "bossKey", ""),
                    BossOrder = IntNumber(row, "bossOrder", 0)
                });
            }

            foreach (var variant in catalog.Variants)
            {
                var difficulty = catalog.Difficulties.FirstOrDefault(item => String.Equals(item.DifficultyKey, variant.DifficultyKey, StringComparison.Ordinal));
                variant.DifficultyName = difficulty == null ? variant.DifficultyKey : difficulty.DisplayName;
            }
            catalog.Contents = catalog.Contents.OrderBy(item => item.DisplayOrder).ThenBy(item => item.DisplayName).ToList();
            catalog.Dungeons = catalog.Dungeons.OrderBy(item => item.ContentKey).ThenBy(item => item.Tier).ThenBy(item => item.OrderInTier).ToList();
            catalog.Variants = catalog.Variants.OrderBy(item => item.ContentKey).ThenBy(item => item.Tier).ThenBy(item =>
            {
                var difficulty = catalog.Difficulties.FirstOrDefault(value => value.DifficultyKey == item.DifficultyKey);
                return difficulty == null ? 0 : difficulty.DisplayOrder;
            }).ToList();
            return catalog;
        }

        private static MeterUpdateManifest ParseUpdate(Dictionary<string, object> node)
        {
            if (node == null) return null;
            var value = new MeterUpdateManifest
            {
                Version = Text(node, "version", ""),
                FileVersion = Text(node, "fileVersion", ""),
                MinimumVersion = Text(node, "minimumVersion", ""),
                FileName = Text(node, "fileName", ""),
                DownloadUrl = Text(node, "downloadUrl", ""),
                Sha256 = Text(node, "sha256", ""),
                FileSize = Number(node, "fileSize", 0),
                Mandatory = Bool(node, "mandatory"),
                ReleaseMandatory = Bool(node, "releaseMandatory"),
                ReleaseNote = Text(node, "releaseNote", Text(node, "message", "")),
                PublishedAt = Text(node, "publishedAt", ""),
                Channel = Text(node, "channel", KinojoVersion.Channel)
            };
            return String.IsNullOrWhiteSpace(value.Version) ? null : value;
        }

        private static int RoleLevelFromLabel(string label)
        {
            if (String.Equals(label, "Master", StringComparison.OrdinalIgnoreCase)) return 5;
            if (String.Equals(label, "Sub Master", StringComparison.OrdinalIgnoreCase)) return 4;
            if (String.Equals(label, "Manager", StringComparison.OrdinalIgnoreCase)) return 3;
            if (String.Equals(label, "Staff", StringComparison.OrdinalIgnoreCase)) return 2;
            if (String.Equals(label, "Guest", StringComparison.OrdinalIgnoreCase)) return 0;
            return 1;
        }

        private static CharacterProfile ParseCharacter(Dictionary<string, object> row)
        {
            return new CharacterProfile
            {
                CharacterKey = Text(row, "characterKey", Text(row, "character_key", "")),
                CharacterName = Text(row, "characterName", Text(row, "character_name", "")),
                MainCharacterName = Text(row, "mainCharacterName", Text(row, "main_character_name", "")),
                ServerId = Text(row, "serverId", Text(row, "server_id", "")),
                ServerName = Text(row, "serverName", Text(row, "server_name", "")),
                ClassKey = Text(row, "classKey", Text(row, "class_key", "")),
                ClassName = Text(row, "className", Text(row, "class_name", "미확인")),
                CharKey = Text(row, "charKey", Text(row, "char_key", "")),
                ProfileImageUrl = Text(row, "profileImageUrl", Text(row, "profile_image_url", "")),
                DetailUrl = Text(row, "detailUrl", Text(row, "detail_url", "")),
                PveCombatPower = Number(row, "pveCombatPower", Number(row, "pve_combat_power", 0)),
                IsMain = Bool(row, "isMain") || Bool(row, "is_main")
            };
        }

        private static IEnumerable<Dictionary<string, object>> DictList(Dictionary<string, object> source, string key)
        {
            if (source == null) yield break;
            object raw;
            if (!source.TryGetValue(key, out raw) || raw == null) yield break;
            var list = raw as IEnumerable;
            if (list == null) yield break;
            foreach (var item in list)
            {
                var row = item as Dictionary<string, object>;
                if (row != null) yield return row;
            }
        }

        private static Dictionary<string, object> Dict(Dictionary<string, object> source, string key)
        {
            if (source == null) return null;
            object value;
            return source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static string Text(Dictionary<string, object> source, string key, string fallback)
        {
            if (source == null) return fallback;
            object value;
            return source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : fallback;
        }

        private static bool Bool(Dictionary<string, object> source, string key)
        {
            if (source == null) return false;
            object value;
            if (!source.TryGetValue(key, out value) || value == null) return false;
            bool parsed;
            if (Boolean.TryParse(Convert.ToString(value), out parsed)) return parsed;
            long numeric;
            return Int64.TryParse(Convert.ToString(value), out numeric) && numeric != 0;
        }

        private static long Number(Dictionary<string, object> source, string key, long fallback)
        {
            if (source == null) return fallback;
            object value;
            if (!source.TryGetValue(key, out value) || value == null) return fallback;
            long parsed;
            return Int64.TryParse(Convert.ToString(value), out parsed) ? parsed : fallback;
        }

        private static int IntNumber(Dictionary<string, object> source, string key, int fallback)
        {
            var value = Number(source, key, fallback);
            if (value > Int32.MaxValue) return Int32.MaxValue;
            if (value < Int32.MinValue) return Int32.MinValue;
            return (int)value;
        }
    }
}
