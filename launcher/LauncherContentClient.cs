using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal sealed class LauncherContentClient : IDisposable
    {
        private const string ContentUrl = "https://kinojo.info/launcher-content.json";
        private const string ExpectedHost = "kinojo.info";
        private const int MaximumContentBytes = 256 * 1024;
        private static readonly Regex IdPattern = new Regex(@"^[a-z0-9][a-z0-9-]{2,63}$", RegexOptions.CultureInvariant);
        private static readonly Regex VersionPattern = new Regex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);
        private readonly HttpClient _http = new HttpClient();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = MaximumContentBytes };

        public LauncherContentClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            _http.Timeout = TimeSpan.FromSeconds(6);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "KINOJO-Meter-Launcher/" + LauncherVersion.Current);
        }

        public LauncherContentLoadResult LoadCached()
        {
            try
            {
                if (!File.Exists(LauncherPaths.LauncherContentCacheFile)) return null;
                var cached = File.ReadAllText(LauncherPaths.LauncherContentCacheFile, Encoding.UTF8);
                var result = Parse(cached);
                result.Cached = true;
                result.Status = "저장된 공지";
                return result;
            }
            catch
            {
                return null;
            }
        }

        public async Task<LauncherContentLoadResult> LoadAsync(CancellationToken cancellationToken)
        {
            try
            {
                var raw = await DownloadAsync(cancellationToken).ConfigureAwait(false);
                var result = Parse(raw);
                WriteTextAtomically(LauncherPaths.LauncherContentCacheFile, raw);
                result.Cached = false;
                result.Status = "최신 공지";
                return result;
            }
            catch
            {
                var cached = LoadCached();
                return cached ?? new LauncherContentLoadResult
                {
                    Items = new List<LauncherContentItem>(),
                    Cached = false,
                    Status = "공지를 불러오지 못했습니다"
                };
            }
        }

        private async Task<string> DownloadAsync(CancellationToken cancellationToken)
        {
            var url = ContentUrl + "?launcher=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength.HasValue &&
                    response.Content.Headers.ContentLength.Value > MaximumContentBytes)
                    throw new InvalidOperationException("공지 데이터 크기가 올바르지 않습니다.");
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length > MaximumContentBytes)
                    throw new InvalidOperationException("공지 데이터 크기가 올바르지 않습니다.");
                return new UTF8Encoding(false, true).GetString(bytes);
            }
        }

        public static HashSet<string> ReadSeenIds()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(LauncherPaths.LauncherContentReadFile)) return result;
                var serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 };
                var root = serializer.DeserializeObject(File.ReadAllText(LauncherPaths.LauncherContentReadFile, Encoding.UTF8)) as Dictionary<string, object>;
                object values;
                var enumerable = root != null && root.TryGetValue("ids", out values) ? values as IEnumerable : null;
                if (enumerable == null) return result;
                foreach (var value in enumerable)
                {
                    var id = Convert.ToString(value);
                    if (!String.IsNullOrWhiteSpace(id) && IdPattern.IsMatch(id)) result.Add(id);
                    if (result.Count >= 200) break;
                }
            }
            catch { }
            return result;
        }

        public static void SaveSeenIds(IEnumerable<string> ids)
        {
            try
            {
                var values = (ids ?? Enumerable.Empty<string>())
                    .Where(id => !String.IsNullOrWhiteSpace(id) && IdPattern.IsMatch(id))
                    .Distinct(StringComparer.Ordinal)
                    .Take(200)
                    .ToArray();
                var serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 };
                WriteTextAtomically(LauncherPaths.LauncherContentReadFile, serializer.Serialize(new Dictionary<string, object>
                {
                    { "schemaVersion", 1 },
                    { "ids", values }
                }));
            }
            catch { }
        }

        internal static LauncherContentLoadResult ParseForTest(string raw)
        {
            using (var client = new LauncherContentClient()) return client.Parse(raw);
        }

        private LauncherContentLoadResult Parse(string raw)
        {
            if (String.IsNullOrWhiteSpace(raw) || Encoding.UTF8.GetByteCount(raw) > MaximumContentBytes)
                throw new InvalidOperationException("공지 데이터 크기가 올바르지 않습니다.");
            var root = _json.DeserializeObject(raw) as Dictionary<string, object>;
            if (root == null || Integer(root, "schemaVersion", 0) != 1)
                throw new InvalidOperationException("공지 데이터 버전이 올바르지 않습니다.");

            object rawItems;
            var enumerable = root.TryGetValue("items", out rawItems) ? rawItems as IEnumerable : null;
            if (enumerable == null) throw new InvalidOperationException("공지 목록이 없습니다.");
            var items = new List<LauncherContentItem>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var total = 0;
            foreach (var value in enumerable)
            {
                total += 1;
                if (total > 32) throw new InvalidOperationException("공지 항목이 허용 개수를 초과했습니다.");
                var row = value as Dictionary<string, object>;
                if (row == null) throw new InvalidOperationException("공지 항목 형식이 올바르지 않습니다.");
                var item = ParseItem(row);
                if (!ids.Add(item.Id)) throw new InvalidOperationException("중복된 공지 ID가 있습니다.");
                if (item.Channel == "all" || item.Channel == LauncherVersion.Channel) items.Add(item);
            }

            items.Sort(delegate(LauncherContentItem left, LauncherContentItem right)
            {
                if (left.Pinned != right.Pinned) return left.Pinned ? -1 : 1;
                return right.PublishedAt.CompareTo(left.PublishedAt);
            });
            return new LauncherContentLoadResult { Items = items, Status = "최신 공지" };
        }

        private static LauncherContentItem ParseItem(Dictionary<string, object> row)
        {
            var id = Text(row, "id", "").Trim().ToLowerInvariant();
            var type = Text(row, "type", "").Trim().ToLowerInvariant();
            var channel = Text(row, "channel", "all").Trim().ToLowerInvariant();
            var title = Text(row, "title", "").Trim();
            var summary = Text(row, "summary", "").Trim();
            var version = Text(row, "version", "").Trim();
            var url = Text(row, "url", "").Trim();
            DateTimeOffset publishedAt;
            Uri uri;

            if (!IdPattern.IsMatch(id)) throw new InvalidOperationException("공지 ID가 올바르지 않습니다.");
            if (type != "notice" && type != "update") throw new InvalidOperationException("공지 유형이 올바르지 않습니다.");
            if (channel != "all" && channel != "stable" && channel != "staging") throw new InvalidOperationException("공지 채널이 올바르지 않습니다.");
            if (String.IsNullOrWhiteSpace(title) || title.Length > 120) throw new InvalidOperationException("공지 제목이 올바르지 않습니다.");
            if (summary.Length > 400) throw new InvalidOperationException("공지 요약이 너무 깁니다.");
            if (!DateTimeOffset.TryParse(Text(row, "publishedAt", ""), out publishedAt)) throw new InvalidOperationException("공지 날짜가 올바르지 않습니다.");
            if (!String.IsNullOrWhiteSpace(version) && !VersionPattern.IsMatch(version)) throw new InvalidOperationException("공지 버전이 올바르지 않습니다.");
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !String.Equals(uri.Host, ExpectedHost, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("공지 링크가 허용된 KINOJO 주소가 아닙니다.");

            return new LauncherContentItem
            {
                Id = id,
                Type = type,
                Channel = channel,
                Pinned = Boolean(row, "pinned"),
                Title = title,
                Summary = summary,
                PublishedAt = publishedAt,
                Version = version,
                Url = uri.AbsoluteUri
            };
        }

        private static void WriteTextAtomically(string path, string text)
        {
            LauncherPaths.EnsureDirectories();
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, text ?? "", new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        private static string Text(Dictionary<string, object> source, string key, string fallback)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : fallback;
        }

        private static int Integer(Dictionary<string, object> source, string key, int fallback)
        {
            int parsed;
            return Int32.TryParse(Text(source, key, ""), out parsed) ? parsed : fallback;
        }

        private static bool Boolean(Dictionary<string, object> source, string key)
        {
            bool parsed;
            return System.Boolean.TryParse(Text(source, key, "false"), out parsed) && parsed;
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
