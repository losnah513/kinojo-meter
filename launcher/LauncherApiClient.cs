using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal sealed class LauncherApiClient : IDisposable
    {
        private const string SiteConfigUrl = "https://kinojo.info/config.json";
        private const string ExpectedSupabaseHost = "josvoltpktvwysrasffq.supabase.co";
        private readonly HttpClient _http = new HttpClient();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
        private string _supabaseUrl;
        private string _publishableKey;

        public LauncherApiClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            _http.Timeout = TimeSpan.FromSeconds(20);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "KINOJO-Meter-Launcher/" + LauncherVersion.Current);
        }

        public string ProjectHost
        {
            get
            {
                Uri uri;
                return Uri.TryCreate(_supabaseUrl, UriKind.Absolute, out uri) ? uri.Host : "";
            }
        }

        public string ApiEndpoint
        {
            get { return _supabaseUrl + "/functions/v1/" + LauncherBuildProfile.FunctionName; }
        }

        public async Task<LauncherLoginResult> LoginAsync(string passKey)
        {
            var result = await PostAsync(new Dictionary<string, object>
            {
                { "action", "login" },
                { "passKey", passKey ?? "" },
                { "clientVersion", "LAUNCHER_" + LauncherVersion.Current }
            }).ConfigureAwait(false);
            if (!Bool(result, "ok")) throw new InvalidOperationException(Text(result, "message", "PASS KEY 인증에 실패했습니다."));
            var account = Dict(result, "account");
            return new LauncherLoginResult
            {
                SessionToken = Text(result, "sessionToken", ""),
                DisplayName = Text(account, "mainCharacterName", Text(account, "mainCharacter", "KINOJO 사용자")),
                Account = account == null ? new Dictionary<string, object>() : account,
                Characters = DictList(result, "characters")
            };
        }

        public async Task<CoreUpdateAuthorization> AuthorizeCoreUpdateAsync(
            string sessionToken,
            string installationId,
            string currentCoreVersion)
        {
            var result = await PostAsync(new Dictionary<string, object>
            {
                { "action", "coreUpdateAuthorization" },
                { "sessionToken", sessionToken ?? "" },
                { "installationId", installationId ?? "" },
                { "launcherVersion", LauncherVersion.Current },
                { "currentCoreVersion", currentCoreVersion ?? "" },
                { "channel", LauncherVersion.Channel }
            }).ConfigureAwait(false);

            var authorization = new CoreUpdateAuthorization
            {
                Authorized = Bool(result, "authorized") || (Bool(result, "ok") && Dict(result, "coreRelease") != null),
                BlockedByOperation = Bool(result, "blockedByOperation"),
                Code = Text(result, "code", ""),
                Message = Text(result, "message", "")
            };
            var release = Dict(result, "coreRelease");
            if (release != null) authorization.Release = ParseCoreRelease(release);
            if (!Bool(result, "ok") && String.IsNullOrWhiteSpace(authorization.Message))
                authorization.Message = "Core 업데이트 승인을 받지 못했습니다.";
            return authorization;
        }

        public async Task<LauncherUpdateCheckResult> CheckLauncherUpdateAsync()
        {
            var result = await PostAsync(new Dictionary<string, object>
            {
                { "action", "distributionManifest" },
                { "channel", LauncherVersion.Channel },
                { "launcherVersion", LauncherVersion.Current }
            }).ConfigureAwait(false);
            if (!Bool(result, "ok"))
                throw new InvalidOperationException(Text(result, "message", "Launcher 업데이트 정보를 확인하지 못했습니다."));
            return ParseLauncherUpdate(result);
        }

        public async Task LogoutAsync(string sessionToken)
        {
            if (String.IsNullOrWhiteSpace(sessionToken)) return;
            try
            {
                await PostAsync(new Dictionary<string, object>
                {
                    { "action", "logout" },
                    { "sessionToken", sessionToken }
                }).ConfigureAwait(false);
            }
            catch { }
        }

        private async Task EnsureConfigAsync()
        {
            if (!String.IsNullOrWhiteSpace(_supabaseUrl) && !String.IsNullOrWhiteSpace(_publishableKey)) return;
            var text = await _http.GetStringAsync(SiteConfigUrl + "?launcher=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ConfigureAwait(false);
            var config = _json.DeserializeObject(text) as Dictionary<string, object>;
            var supabase = Dict(config, "supabase");
            _supabaseUrl = Text(supabase, "url", "").TrimEnd('/');
            _publishableKey = Text(supabase, "publishableKey", "");
            Uri uri;
            if (!Uri.TryCreate(_supabaseUrl, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !String.Equals(uri.Host, ExpectedSupabaseHost, StringComparison.OrdinalIgnoreCase) ||
                String.IsNullOrWhiteSpace(_publishableKey))
                throw new InvalidOperationException("KINOJO 서버 연결 설정이 올바르지 않습니다.");
        }

        private async Task<Dictionary<string, object>> PostAsync(Dictionary<string, object> payload)
        {
            await EnsureConfigAsync().ConfigureAwait(false);
            using (var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint))
            {
                request.Headers.TryAddWithoutValidation("apikey", _publishableKey);
                request.Content = new StringContent(_json.Serialize(payload), Encoding.UTF8, "application/json");
                using (var response = await _http.SendAsync(request).ConfigureAwait(false))
                {
                    var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Dictionary<string, object> result;
                    try { result = _json.DeserializeObject(raw) as Dictionary<string, object>; }
                    catch { result = null; }
                    if (result == null) result = new Dictionary<string, object>();
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException(Text(result, "message", "서버 HTTP " + (int)response.StatusCode));
                    return result;
                }
            }
        }

        private static CoreReleaseManifest ParseCoreRelease(Dictionary<string, object> value)
        {
            DateTimeOffset expiresAt;
            if (!DateTimeOffset.TryParse(Text(value, "expiresAt", ""), out expiresAt)) expiresAt = DateTimeOffset.MinValue;
            return new CoreReleaseManifest
            {
                SchemaVersion = Int(value, "schemaVersion", 1),
                Channel = Text(value, "channel", LauncherVersion.Channel),
                CoreVersion = Text(value, "coreVersion", ""),
                MinimumCoreVersion = Text(value, "minimumCoreVersion", ""),
                MinimumLauncherVersion = Text(value, "minimumLauncherVersion", ""),
                PackageId = Text(value, "packageId", ""),
                FileName = Text(value, "fileName", ""),
                FileSize = Long(value, "fileSize", 0),
                Sha256 = Text(value, "sha256", "").ToLowerInvariant(),
                InstallManifestSha256 = Text(value, "installManifestSha256", "").ToLowerInvariant(),
                DownloadUrl = Text(value, "downloadUrl", ""),
                ExpiresAt = expiresAt,
                EntryPoint = Text(value, "entryPoint", LauncherBuildProfile.CoreEntryPoint),
                Mandatory = Bool(value, "mandatory"),
                ReleaseNote = Text(value, "releaseNote", ""),
                CodeSignatureRequired = Bool(value, "codeSignatureRequired"),
                PublisherSubject = Text(value, "publisherSubject", ""),
                IntegrityMode = Text(value, "integrityMode", ""),
                SigningKeyId = Text(value, "signingKeyId", ""),
                ManifestSignature = Text(value, "manifestSignature", "")
            };
        }

        internal static LauncherUpdateCheckResult ParseLauncherUpdateForTest(Dictionary<string, object> value)
        {
            return ParseLauncherUpdate(value);
        }

        private static LauncherUpdateCheckResult ParseLauncherUpdate(Dictionary<string, object> value)
        {
            var releaseEnvelope = Dict(value, "launcherRelease");
            var release = Dict(releaseEnvelope, "launcherUpdate");
            return new LauncherUpdateCheckResult
            {
                ReleaseAvailable = Bool(releaseEnvelope, "releaseAvailable"),
                UpdateAvailable = Bool(releaseEnvelope, "updateAvailable"),
                Release = release == null ? null : new LauncherUpdateManifest
                {
                    SchemaVersion = 1,
                    Channel = Text(release, "channel", LauncherVersion.Channel),
                    Version = Text(release, "version", ""),
                    FileVersion = Text(release, "fileVersion", ""),
                    MinimumVersion = Text(release, "minimumVersion", ""),
                    FileName = Text(release, "fileName", ""),
                    FileSize = Long(release, "fileSize", 0),
                    Sha256 = Text(release, "sha256", "").ToLowerInvariant(),
                    DownloadUrl = Text(release, "downloadUrl", ""),
                    Mandatory = Bool(release, "mandatory"),
                    ReleaseNote = Text(release, "releaseNote", ""),
                    CodeSignatureRequired = Bool(release, "codeSignatureRequired"),
                    PublisherSubject = Text(release, "publisherSubject", ""),
                    TrustMode = Text(release, "trustMode", ""),
                    SmartScreenWarningExpected = Bool(release, "smartScreenWarningExpected")
                }
            };
        }

        private static Dictionary<string, object> Dict(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static List<Dictionary<string, object>> DictList(Dictionary<string, object> source, string key)
        {
            object value;
            var result = new List<Dictionary<string, object>>();
            if (source == null || !source.TryGetValue(key, out value)) return result;
            var values = value as IEnumerable;
            if (values == null) return result;
            foreach (var item in values)
            {
                var row = item as Dictionary<string, object>;
                if (row != null) result.Add(row);
            }
            return result;
        }

        private static string Text(Dictionary<string, object> source, string key, string fallback)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : fallback;
        }

        private static bool Bool(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return false;
            bool parsed;
            if (Boolean.TryParse(Convert.ToString(value), out parsed)) return parsed;
            long number;
            return Int64.TryParse(Convert.ToString(value), out number) && number != 0;
        }

        private static int Int(Dictionary<string, object> source, string key, int fallback)
        {
            int parsed;
            return Int32.TryParse(Text(source, key, ""), out parsed) ? parsed : fallback;
        }

        private static long Long(Dictionary<string, object> source, string key, long fallback)
        {
            long parsed;
            return Int64.TryParse(Text(source, key, ""), out parsed) ? parsed : fallback;
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
