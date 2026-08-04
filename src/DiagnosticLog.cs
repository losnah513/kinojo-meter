using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace KinojoMeterPrototype
{
    internal static class DiagnosticLog
    {
        private static readonly object Gate = new object();
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KINOJO Meter",
            "logs");

        public static string CurrentFilePath
        {
            get { return Path.Combine(DirectoryPath, "meter-" + DateTime.Now.ToString("yyyyMMdd") + ".log"); }
        }

        public static void Info(string category, string message)
        {
            Write("INFO", category, message, null);
        }

        public static void Error(string category, string message, Exception exception)
        {
            Write("ERROR", category, message, exception);
        }

        public static void OpenFolder()
        {
            Directory.CreateDirectory(DirectoryPath);
            Process.Start(new ProcessStartInfo(DirectoryPath) { UseShellExecute = true });
        }

        public static string SaveEncounterSnapshot(CombatSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsCleared) return "";
            try
            {
                var durationMs = snapshot.StartedAtUtc == DateTime.MinValue || snapshot.LastEventUtc == DateTime.MinValue
                    ? 0L
                    : Math.Max(0L, (long)(snapshot.LastEventUtc - snapshot.StartedAtUtc).TotalMilliseconds);
                var participants = snapshot.Rows
                    .Where(row => row != null && !row.IsEmpty)
                    .Select(row => (object)new Dictionary<string, object>
                    {
                        { "participantKey", row.ParticipantKey ?? "" },
                        { "characterName", row.Name ?? "" },
                        { "serverId", row.ServerId ?? "" },
                        { "serverName", row.ServerName ?? "" },
                        { "classKey", row.ClassKey ?? "" },
                        { "className", row.ClassName ?? "" },
                        { "classRaw", row.ClassRaw },
                        { "pveCombatPower", row.CombatPower },
                        { "partyNumber", row.PartyNumber },
                        { "partySlot", row.PartySlot },
                        { "totalDamage", row.TotalDamage },
                        { "dps", row.Dps },
                        { "damageShare", row.Share },
                        { "isSelf", row.IsSelf }
                    })
                    .ToList();
                var document = new Dictionary<string, object>
                {
                    { "schemaVersion", 1 },
                    { "savedAt", DateTime.UtcNow.ToString("o") },
                    { "startedAt", snapshot.StartedAtUtc == DateTime.MinValue ? "" : snapshot.StartedAtUtc.ToUniversalTime().ToString("o") },
                    { "endedAt", snapshot.LastEventUtc == DateTime.MinValue ? "" : snapshot.LastEventUtc.ToUniversalTime().ToString("o") },
                    { "durationMs", durationMs },
                    { "dungeonKey", snapshot.DungeonKey ?? "" },
                    { "dungeonName", snapshot.DungeonName ?? "" },
                    { "difficultyKey", snapshot.DifficultyKey ?? "" },
                    { "difficultyName", snapshot.DifficultyName ?? "" },
                    { "bossOrder", snapshot.BossOrder },
                    { "bossScopedId", snapshot.BossId ?? "" },
                    { "bossRuntimeId", snapshot.BossRuntimeId },
                    { "bossName", snapshot.BossName ?? "" },
                    { "bossIdentityMode", snapshot.BossIdentityMode ?? "" },
                    { "bossHpSource", snapshot.BossHpSource ?? "" },
                    { "completionMode", snapshot.CompletionMode ?? "" },
                    { "bossObservedMaxHp", snapshot.BossMaxHp },
                    { "bossCurrentHp", snapshot.BossCurrentHp },
                    { "captureEngine", snapshot.CaptureEngine ?? "" },
                    { "captureMode", snapshot.CaptureMode ?? "" },
                    { "decoderType", snapshot.DecoderType ?? "" },
                    { "decoderVersion", snapshot.DecoderVersion ?? "" },
                    { "decoderValidated", snapshot.DecoderValidated },
                    { "uploadEligible", snapshot.UploadEligible },
                    { "participants", participants }
                };
                var path = Path.Combine(DirectoryPath, "encounters-" + DateTime.Now.ToString("yyyyMMdd") + ".jsonl");
                var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(document);
                lock (Gate)
                {
                    Directory.CreateDirectory(DirectoryPath);
                    File.AppendAllText(path, json + Environment.NewLine, new UTF8Encoding(false));
                }
                return path;
            }
            catch (Exception ex)
            {
                Error("LOCAL_RESULT", "Encounter snapshot persistence failed", ex);
                return "";
            }
        }

        private static void Write(string level, string category, string message, Exception exception)
        {
            try
            {
                var builder = new StringBuilder();
                builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                builder.Append(" [").Append(level).Append("] ");
                builder.Append(String.IsNullOrWhiteSpace(category) ? "GENERAL" : category.Trim());
                builder.Append(" · ").Append(String.IsNullOrWhiteSpace(message) ? "-" : message.Replace("\r", " ").Replace("\n", " "));
                if (exception != null) builder.AppendLine().Append(exception);
                builder.AppendLine();
                lock (Gate)
                {
                    Directory.CreateDirectory(DirectoryPath);
                    File.AppendAllText(CurrentFilePath, builder.ToString(), Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    internal sealed class DiagnosticFrameCollector : IDisposable
    {
        private const long MaximumBytes = 64L * 1024L * 1024L;
        private const int MaximumChunks = 100000;
        private static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(20);
        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _connectionAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private FileStream _payload;
        private StreamWriter _index;
        private StreamWriter _markers;
        private DateTime _startedAtUtc;
        private long _writtenBytes;
        private int _chunkCount;
        private string _sessionDirectory = "";

        public bool IsActive { get { lock (_gate) return _payload != null; } }
        public string SessionDirectory { get { lock (_gate) return _sessionDirectory; } }

        public string Start()
        {
            lock (_gate)
            {
                StopLocked("RESTARTED");
                _startedAtUtc = DateTime.UtcNow;
                _writtenBytes = 0;
                _chunkCount = 0;
                _connectionAliases.Clear();
                _sessionDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "KINOJO Meter",
                    "packet-fixtures",
                    "fixture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(_sessionDirectory);
                _payload = new FileStream(Path.Combine(_sessionDirectory, "frames.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _index = new StreamWriter(Path.Combine(_sessionDirectory, "frames.tsv"), false, new UTF8Encoding(false));
                _markers = new StreamWriter(Path.Combine(_sessionDirectory, "markers.tsv"), false, new UTF8Encoding(false));
                _index.WriteLine("chunk\tcaptured_at_utc\tconnection_id\tdirection\tsequence\tlength\toffset");
                _markers.WriteLine("captured_at_utc\tmarker\tdetail");
                File.WriteAllText(
                    Path.Combine(_sessionDirectory, "README.txt"),
                    "KINOJO Meter decoder fixture\r\n" +
                    "수집 데이터는 자동 업로드되지 않습니다.\r\n" +
                    "frames.bin에는 전투 중 TCP payload 원본이 포함되며 캐릭터명 등 게임 데이터가 들어 있을 수 있습니다.\r\n" +
                    "IP/포트는 frames.tsv에 기록하지 않고 양방향 연결 단위의 세션 전용 connection_id로 치환됩니다.\r\n" +
                    "direction은 동일 connection_id의 양방향을 A_TO_B/B_TO_A로 구분하며 TCP sequence가 함께 기록됩니다.\r\n" +
                    "수집 시작 전 최근 최대 2분/8MiB의 순환 버퍼가 먼저 기록될 수 있습니다.\r\n" +
                    "수집 제한은 최대 20분/64MiB/100,000조각입니다.\r\n",
                    new UTF8Encoding(false));
                return _sessionDirectory;
            }
        }

        public void Append(CapturedTcpPayloadEventArgs segment)
        {
            if (segment == null || segment.Payload == null || segment.Payload.Length == 0) return;
            lock (_gate)
            {
                if (_payload == null) return;
                if (_chunkCount >= MaximumChunks ||
                    _writtenBytes + segment.Payload.Length > MaximumBytes ||
                    DateTime.UtcNow - _startedAtUtc >= MaximumDuration)
                {
                    StopLocked("LIMIT_REACHED");
                    return;
                }

                string connectionId;
                if (!_connectionAliases.TryGetValue(segment.ConnectionKey, out connectionId))
                {
                    connectionId = "connection-" + (_connectionAliases.Count + 1).ToString("D3") + "-" + ShortHash(segment.ConnectionKey);
                    _connectionAliases[segment.ConnectionKey] = connectionId;
                }

                var offset = _writtenBytes;
                _payload.Write(segment.Payload, 0, segment.Payload.Length);
                _writtenBytes += segment.Payload.Length;
                _chunkCount++;
                _index.WriteLine(_chunkCount + "\t" +
                    segment.TimestampUtc.ToUniversalTime().ToString("o") + "\t" +
                    connectionId + "\t" + segment.Direction + "\t" + segment.SequenceNumber + "\t" +
                    segment.Payload.Length + "\t" + offset);
                if (_chunkCount % 50 == 0)
                {
                    _payload.Flush();
                    _index.Flush();
                }
            }
        }

        public bool AddMarker(string marker, string detail)
        {
            lock (_gate)
            {
                if (_markers == null) return false;
                var safeMarker = SanitizeTsv(marker);
                if (String.IsNullOrWhiteSpace(safeMarker)) return false;
                _markers.WriteLine(DateTime.UtcNow.ToString("o") + "\t" + safeMarker + "\t" + SanitizeTsv(detail));
                _markers.Flush();
                return true;
            }
        }

        public string Stop()
        {
            lock (_gate)
            {
                var directory = _sessionDirectory;
                StopLocked("STOPPED_BY_USER");
                return directory;
            }
        }

        private void StopLocked(string reason)
        {
            if (_payload == null && _index == null) return;
            try
            {
                if (_markers != null)
                {
                    _markers.WriteLine(DateTime.UtcNow.ToString("o") + "\tCAPTURE_ENDED\t" + reason);
                    _markers.Flush();
                    _markers.Dispose();
                }
            }
            catch { }
            try
            {
                if (_index != null)
                {
                    _index.WriteLine("# result\t" + reason + "\tchunks=" + _chunkCount + "\tbytes=" + _writtenBytes);
                    _index.Flush();
                    _index.Dispose();
                }
            }
            catch { }
            try
            {
                if (_payload != null)
                {
                    _payload.Flush();
                    _payload.Dispose();
                }
            }
            catch { }
            _index = null;
            _payload = null;
            _markers = null;
        }

        private static string SanitizeTsv(string value)
        {
            return (value ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static string ShortHash(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                var builder = new StringBuilder(12);
                for (var index = 0; index < 6; index++) builder.Append(bytes[index].ToString("x2"));
                return builder.ToString();
            }
        }

        public void Dispose()
        {
            lock (_gate) StopLocked("DISPOSED");
        }
    }
}
