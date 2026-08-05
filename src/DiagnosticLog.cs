using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
        private static readonly ConcurrentQueue<string> PendingLines = new ConcurrentQueue<string>();
        private static readonly System.Threading.AutoResetEvent PendingSignal = new System.Threading.AutoResetEvent(false);
        private static readonly System.Threading.Thread WriterThread;
        private static int PendingLineCount;
        private static int DroppedLineCount;
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KINOJO Meter",
            "logs");

        static DiagnosticLog()
        {
            WriterThread = new System.Threading.Thread(WriteLoop)
            {
                IsBackground = true,
                Name = "KINOJO-Diagnostic-Writer",
                Priority = System.Threading.ThreadPriority.BelowNormal
            };
            WriterThread.Start();
        }

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
                        { "platformCharacterId", row.PlatformCharacterId ?? "" },
                        { "characterName", row.Name ?? "" },
                        { "serverId", row.ServerId ?? "" },
                        { "serverName", row.ServerName ?? "" },
                        { "classKey", row.ClassKey ?? "" },
                        { "className", row.ClassName ?? "" },
                        { "classRaw", row.ClassRaw },
                        { "pveCombatPower", row.CombatPower },
                        { "itemLevel", row.ItemLevel },
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

        public static string SaveEncounterOutbox(CombatSnapshot snapshot, CharacterProfile selected, string processingMode)
        {
            if (snapshot == null || !snapshot.IsCleared) return "";
            try
            {
                var participants = snapshot.Rows
                    .Where(row => row != null && !row.IsEmpty)
                    .Select(row => (object)new Dictionary<string, object>
                    {
                        { "participantKey", row.ParticipantKey ?? "" },
                        { "platformCharacterId", row.PlatformCharacterId ?? "" },
                        { "characterName", row.Name ?? "" },
                        { "serverId", row.ServerId ?? "" },
                        { "serverName", row.ServerName ?? "" },
                        { "classKey", row.ClassKey ?? "" },
                        { "className", row.ClassName ?? "" },
                        { "classRaw", row.ClassRaw },
                        { "pveCombatPower", row.CombatPower },
                        { "itemLevel", row.ItemLevel },
                        { "partyNumber", row.PartyNumber },
                        { "partySlot", row.PartySlot },
                        { "totalDamage", row.TotalDamage },
                        { "dps", row.Dps },
                        { "damageShare", row.Share },
                        { "isSelf", row.IsSelf }
                    }).ToList();
                var document = new Dictionary<string, object>
                {
                    { "schemaVersion", 1 },
                    { "processingMode", String.IsNullOrWhiteSpace(processingMode) ? "SIMULATED" : processingMode.Trim().ToUpperInvariant() },
                    { "processingStatus", "LOCAL_STAGED" },
                    { "canonicalStatisticsSubmissionBlocked", !snapshot.UploadEligible },
                    { "observedServerSubmissionEnabled", !snapshot.UploadEligible && String.Equals(snapshot.CaptureMode, "ACTUAL", StringComparison.OrdinalIgnoreCase) },
                    { "damageCompleteness", snapshot.DecoderValidated ? "DECODER_VALIDATED" : "PARTIAL_OPCODE_COVERAGE" },
                    { "stagedAt", DateTime.UtcNow.ToString("o") },
                    { "selectedCharacterKey", selected == null ? "" : selected.CharacterKey ?? "" },
                    { "selectedCharacterName", selected == null ? "" : selected.CharacterName ?? "" },
                    { "dungeonKey", snapshot.DungeonKey ?? "" },
                    { "dungeonName", snapshot.DungeonName ?? "" },
                    { "difficultyKey", snapshot.DifficultyKey ?? "" },
                    { "difficultyName", snapshot.DifficultyName ?? "" },
                    { "bossOrder", snapshot.BossOrder },
                    { "bossScopedId", snapshot.BossId ?? "" },
                    { "bossRuntimeId", snapshot.BossRuntimeId },
                    { "bossName", snapshot.BossName ?? "" },
                    { "bossIdentityMode", snapshot.BossIdentityMode ?? "" },
                    { "completionMode", snapshot.CompletionMode ?? "" },
                    { "bossObservedMaxHp", snapshot.BossMaxHp },
                    { "bossCurrentHp", snapshot.BossCurrentHp },
                    { "startedAt", snapshot.StartedAtUtc == DateTime.MinValue ? "" : snapshot.StartedAtUtc.ToUniversalTime().ToString("o") },
                    { "endedAt", snapshot.LastEventUtc == DateTime.MinValue ? "" : snapshot.LastEventUtc.ToUniversalTime().ToString("o") },
                    { "captureEngine", snapshot.CaptureEngine ?? "" },
                    { "captureMode", snapshot.CaptureMode ?? "" },
                    { "decoderType", snapshot.DecoderType ?? "" },
                    { "decoderVersion", snapshot.DecoderVersion ?? "" },
                    { "decoderValidated", snapshot.DecoderValidated },
                    { "participants", participants }
                };
                var outboxDirectory = Path.Combine(DirectoryPath, "outbox");
                var runtime = snapshot.BossRuntimeId > 0 ? snapshot.BossRuntimeId.ToString() : "unknown";
                var path = Path.Combine(outboxDirectory, "encounter-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") +
                    "-boss" + Math.Max(0, snapshot.BossOrder) + "-" + runtime + ".json");
                var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(document);
                lock (Gate)
                {
                    Directory.CreateDirectory(outboxDirectory);
                    File.WriteAllText(path, json, new UTF8Encoding(false));
                }
                return path;
            }
            catch (Exception ex)
            {
                Error("OUTBOX", "Encounter outbox persistence failed", ex);
                return "";
            }
        }

        public static string SaveSubmissionOutbox(Dictionary<string, object> payload, CharacterProfile selected, bool canonical)
        {
            if (payload == null) return "";
            try
            {
                object rawSourceEventId;
                payload.TryGetValue("sourceEventId", out rawSourceEventId);
                var sourceEventId = Convert.ToString(rawSourceEventId) ?? "";
                var safeId = new string(sourceEventId.Where(character => Char.IsLetterOrDigit(character) || character == '_' || character == '-').ToArray());
                if (String.IsNullOrWhiteSpace(safeId)) safeId = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                var document = new Dictionary<string, object>
                {
                    { "schemaVersion", 1 },
                    { "processingStatus", "PENDING" },
                    { "submissionAction", canonical ? "submitEncounter" : "submitObservedEncounter" },
                    { "selectedCharacterKey", selected == null ? "" : selected.CharacterKey ?? "" },
                    { "selectedCharacterName", selected == null ? "" : selected.CharacterName ?? "" },
                    { "attempts", 0 },
                    { "lastErrorCode", "" },
                    { "lastErrorMessage", "" },
                    { "stagedAt", DateTime.UtcNow.ToString("o") },
                    { "payload", payload }
                };
                var outboxDirectory = Path.Combine(DirectoryPath, "outbox");
                var path = Path.Combine(outboxDirectory, "submission-" + safeId + ".json");
                lock (Gate)
                {
                    Directory.CreateDirectory(outboxDirectory);
                    if (!File.Exists(path)) File.WriteAllText(path, new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(document), new UTF8Encoding(false));
                }
                return path;
            }
            catch (Exception ex)
            {
                Error("OUTBOX", "Submission outbox persistence failed", ex);
                return "";
            }
        }

        public static List<string> PendingSubmissionOutboxPaths()
        {
            try
            {
                var directory = Path.Combine(DirectoryPath, "outbox");
                if (!Directory.Exists(directory)) return new List<string>();
                return Directory.GetFiles(directory, "submission-*.json").OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { return new List<string>(); }
        }

        public static Dictionary<string, object> ReadSubmissionOutbox(string path)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                lock (Gate) return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            }
            catch (Exception ex) { Error("OUTBOX", "Submission outbox read failed", ex); return null; }
        }

        public static void UpdateSubmissionOutbox(string path, string status, string errorCode, string errorMessage)
        {
            try
            {
                lock (Gate)
                {
                    if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
                    var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
                    var document = serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                    if (document == null) return;
                    object attempts;
                    int parsedAttempts;
                    document.TryGetValue("attempts", out attempts);
                    Int32.TryParse(Convert.ToString(attempts), out parsedAttempts);
                    document["attempts"] = parsedAttempts + 1;
                    document["processingStatus"] = String.IsNullOrWhiteSpace(status) ? "PENDING" : status.Trim().ToUpperInvariant();
                    document["lastAttemptAt"] = DateTime.UtcNow.ToString("o");
                    document["lastErrorCode"] = errorCode ?? "";
                    document["lastErrorMessage"] = errorMessage ?? "";
                    File.WriteAllText(path, serializer.Serialize(document), new UTF8Encoding(false));
                }
            }
            catch (Exception ex) { Error("OUTBOX", "Submission outbox status update failed", ex); }
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
                if (System.Threading.Interlocked.Increment(ref PendingLineCount) > 20000)
                {
                    System.Threading.Interlocked.Decrement(ref PendingLineCount);
                    System.Threading.Interlocked.Increment(ref DroppedLineCount);
                    return;
                }
                PendingLines.Enqueue(builder.ToString());
                PendingSignal.Set();
            }
            catch { }
        }

        private static void WriteLoop()
        {
            while (true)
            {
                PendingSignal.WaitOne(500);
                try
                {
                    var builder = new StringBuilder();
                    string line;
                    var batch = 0;
                    while (batch < 4096 && PendingLines.TryDequeue(out line))
                    {
                        batch++;
                        builder.Append(line);
                        System.Threading.Interlocked.Decrement(ref PendingLineCount);
                    }
                    var dropped = System.Threading.Interlocked.Exchange(ref DroppedLineCount, 0);
                    if (dropped > 0) builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                        .Append(" [WARN] LOG · dropped background lines=").Append(dropped).AppendLine();
                    if (builder.Length == 0) continue;
                    lock (Gate)
                    {
                        Directory.CreateDirectory(DirectoryPath);
                        File.AppendAllText(CurrentFilePath, builder.ToString(), Encoding.UTF8);
                    }
                }
                catch { }
            }
        }
    }

    internal sealed class DiagnosticFrameCollector : IDisposable
    {
        private const long MaximumBytes = 64L * 1024L * 1024L;
        private const int MaximumChunks = 100000;
        private static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(20);
        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _connectionAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<CapturedTcpPayloadEventArgs> _pending = new ConcurrentQueue<CapturedTcpPayloadEventArgs>();
        private readonly ConcurrentQueue<Tuple<DateTime, string, string>> _pendingMarkers = new ConcurrentQueue<Tuple<DateTime, string, string>>();
        private readonly System.Threading.AutoResetEvent _pendingSignal = new System.Threading.AutoResetEvent(false);
        private readonly System.Threading.Thread _writer;
        private FileStream _payload;
        private StreamWriter _index;
        private StreamWriter _markers;
        private DateTime _startedAtUtc;
        private long _writtenBytes;
        private int _chunkCount;
        private int _pendingCount;
        private int _pendingMarkerCount;
        private int _droppedCount;
        private volatile bool _disposeRequested;
        private bool _accepting;
        private string _sessionDirectory = "";

        public DiagnosticFrameCollector()
        {
            _writer = new System.Threading.Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "KINOJO-Fixture-Writer",
                Priority = System.Threading.ThreadPriority.BelowNormal
            };
            _writer.Start();
        }

        public bool IsActive { get { lock (_gate) return _payload != null; } }
        public string SessionDirectory { get { lock (_gate) return _sessionDirectory; } }

        public string Start()
        {
            lock (_gate) _accepting = false;
            FlushPending(TimeSpan.FromSeconds(2));
            lock (_gate)
            {
                ClearPending();
                StopLocked("RESTARTED");
                _startedAtUtc = DateTime.UtcNow;
                _writtenBytes = 0;
                _chunkCount = 0;
                _droppedCount = 0;
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
                _accepting = true;
                return _sessionDirectory;
            }
        }

        public void Append(CapturedTcpPayloadEventArgs segment)
        {
            if (segment == null || segment.Payload == null || segment.Payload.Length == 0) return;
            lock (_gate)
            {
                if (_payload == null || !_accepting) return;
            }
            if (System.Threading.Interlocked.Increment(ref _pendingCount) > 20000)
            {
                System.Threading.Interlocked.Decrement(ref _pendingCount);
                System.Threading.Interlocked.Increment(ref _droppedCount);
                return;
            }
            _pending.Enqueue(segment);
            _pendingSignal.Set();
        }

        private void AppendCore(CapturedTcpPayloadEventArgs segment)
        {
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

        private void WriterLoop()
        {
            while (!_disposeRequested)
            {
                CapturedTcpPayloadEventArgs segment;
                var wrote = false;
                var batch = 0;
                while (batch < 2048 && _pending.TryDequeue(out segment))
                {
                    batch++;
                    wrote = true;
                    try { AppendCore(segment); }
                    catch { }
                    finally { System.Threading.Interlocked.Decrement(ref _pendingCount); }
                }
                Tuple<DateTime, string, string> marker;
                while (_pendingMarkers.TryDequeue(out marker))
                {
                    wrote = true;
                    try
                    {
                        lock (_gate)
                        {
                            if (_markers != null) _markers.WriteLine(marker.Item1.ToString("o") + "\t" + marker.Item2 + "\t" + marker.Item3);
                        }
                    }
                    finally { System.Threading.Interlocked.Decrement(ref _pendingMarkerCount); }
                }
                if (!wrote) _pendingSignal.WaitOne(250);
            }
        }

        public bool AddMarker(string marker, string detail)
        {
            var safeMarker = SanitizeTsv(marker);
            if (String.IsNullOrWhiteSpace(safeMarker)) return false;
            lock (_gate)
            {
                if (_markers == null || !_accepting) return false;
            }
            if (System.Threading.Interlocked.Increment(ref _pendingMarkerCount) > 1000)
            {
                System.Threading.Interlocked.Decrement(ref _pendingMarkerCount);
                System.Threading.Interlocked.Increment(ref _droppedCount);
                return false;
            }
            _pendingMarkers.Enqueue(Tuple.Create(DateTime.UtcNow, safeMarker, SanitizeTsv(detail)));
            _pendingSignal.Set();
            return true;
        }

        public string Stop()
        {
            lock (_gate) _accepting = false;
            FlushPending(TimeSpan.FromSeconds(2));
            lock (_gate)
            {
                var directory = _sessionDirectory;
                StopLocked("STOPPED_BY_USER");
                return directory;
            }
        }

        private void StopLocked(string reason)
        {
            _accepting = false;
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
                    _index.WriteLine("# result\t" + reason + "\tchunks=" + _chunkCount + "\tbytes=" + _writtenBytes + "\tdropped=" + _droppedCount);
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

        private void FlushPending(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while ((System.Threading.Volatile.Read(ref _pendingCount) > 0 || System.Threading.Volatile.Read(ref _pendingMarkerCount) > 0) && DateTime.UtcNow < deadline)
            {
                _pendingSignal.Set();
                System.Threading.Thread.Yield();
            }
        }

        private void ClearPending()
        {
            CapturedTcpPayloadEventArgs ignored;
            while (_pending.TryDequeue(out ignored)) System.Threading.Interlocked.Decrement(ref _pendingCount);
            Tuple<DateTime, string, string> ignoredMarker;
            while (_pendingMarkers.TryDequeue(out ignoredMarker)) System.Threading.Interlocked.Decrement(ref _pendingMarkerCount);
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
            lock (_gate) _accepting = false;
            FlushPending(TimeSpan.FromSeconds(2));
            lock (_gate) StopLocked("DISPOSED");
            _disposeRequested = true;
            _pendingSignal.Set();
            try { if (_writer.IsAlive) _writer.Join(1000); }
            catch { }
            _pendingSignal.Dispose();
        }
    }
}
