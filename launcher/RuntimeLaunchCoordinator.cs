using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal sealed class RuntimeLaunchSessionPlan
    {
        public PrivateRuntimeProcessPlan ProcessPlan { get; set; }
        public string LaunchId { get; set; }
        public string IpcScopeToken { get; set; }
        public string IssuedAtUtc { get; set; }
        public string ShellSessionLine { get; set; }
        public string EngineHostSessionLine { get; set; }
    }

    internal sealed class SplitRuntimeLaunchResult
    {
        public string LaunchId { get; set; }
        public int ShellProcessId { get; set; }
        public int EngineHostProcessId { get; set; }
        public string RuntimeBundleRevision { get; set; }
        public string RuntimeBundleLockSha256 { get; set; }
    }

    internal sealed class RuntimeProcessStartSpec
    {
        public string Target { get; set; }
        public string Executable { get; set; }
        public string WorkingDirectory { get; set; }
        public bool ShowWindow { get; set; }
        public bool RedirectStandardInput { get; set; }
        public string Arguments { get; set; }
    }

    internal interface IRuntimeChildProcess : IDisposable
    {
        int Id { get; }
        bool HasExited { get; }
        int ExitCode { get; }
        Task WriteSessionLineAsync(string line);
    }

    internal interface IRuntimeProcessLauncher
    {
        IRuntimeChildProcess Start(RuntimeProcessStartSpec spec);
    }

    internal static class RuntimeLaunchCoordinator
    {
        internal const string Status = "PUBLIC_RUNTIME_COORDINATOR_VERIFIED_JOINT_CUTOVER_PENDING";
        internal const string ShellSessionPrefix = "KINOJO_METER_SHELL_SESSION_V1 ";
        internal const string EngineHostSessionPrefix = "KINOJO_METER_ENGINE_HOST_SESSION_V1 ";
        internal const string SessionTransport = "REDIRECTED_STANDARD_INPUT";
        internal const bool PublicLauncherOperationalFlowChanged = true;
        internal const bool LegacyCoreFallbackWithoutActiveBundle = true;
        internal const bool LegacyCoreFallbackWithActiveBundle = false;
        internal const bool JointCutoverEvidenceComplete = false;

        private const int SessionContractVersion = 1;
        private const int MaximumDecodedSessionBytes = 1024 * 1024;
        private const int MaximumEncodedSessionChars = 2 * 1024 * 1024;
        private const string ExpectedProjectHost = "josvoltpktvwysrasffq.supabase.co";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = MaximumDecodedSessionBytes * 4,
            RecursionLimit = 256
        };

        public static async Task<SplitRuntimeLaunchResult> TryLaunchAsync(
            LauncherLoginResult login,
            string installationId,
            string apiEndpoint)
        {
            var bundle = ModuleBundleActivator.ReadVerifiedActiveBundle();
            if (bundle == null) return null;

            PrivateRuntimeProcessPlan processPlan;
            using (var shellUpdater = new ShellModuleUpdater())
            using (var runtimeUpdater = new PrivateRuntimePackageUpdater())
            using (var captureUpdater = new CaptureModuleUpdater())
            using (var protocolUpdater = new ProtocolModuleUpdater())
            using (var syncUpdater = new SyncModuleUpdater())
            {
                var shell = shellUpdater.ReadVerifiedActiveState();
                var runtime = runtimeUpdater.ReadVerifiedActiveState();
                var capture = captureUpdater.ReadVerifiedActiveState();
                var protocol = protocolUpdater.ReadVerifiedActiveState();
                var sync = syncUpdater.ReadVerifiedActiveState();
                processPlan = PrivateRuntimeProcessPlanBuilder.Build(shell, runtime, capture, protocol, sync);
            }

            var session = BuildSessionPlan(
                processPlan,
                bundle,
                login,
                installationId,
                apiEndpoint,
                DateTime.UtcNow,
                Guid.NewGuid(),
                Guid.NewGuid().ToString("N"));
            return await LaunchAsync(
                session,
                new SystemRuntimeProcessLauncher(),
                value => Task.Delay(value)).ConfigureAwait(false);
        }

        internal static RuntimeLaunchSessionPlan BuildSessionPlanForTest(
            PrivateRuntimeProcessPlan processPlan,
            ActiveModuleBundleState bundle,
            LauncherLoginResult login,
            string installationId,
            string apiEndpoint,
            DateTime utcNow,
            Guid launchId,
            string ipcScopeToken)
        {
            return BuildSessionPlan(
                processPlan,
                bundle,
                login,
                installationId,
                apiEndpoint,
                utcNow,
                launchId,
                ipcScopeToken);
        }

        internal static Task<SplitRuntimeLaunchResult> LaunchForTestAsync(
            RuntimeLaunchSessionPlan session,
            IRuntimeProcessLauncher launcher,
            Func<TimeSpan, Task> delay)
        {
            return LaunchAsync(session, launcher, delay);
        }

        private static RuntimeLaunchSessionPlan BuildSessionPlan(
            PrivateRuntimeProcessPlan processPlan,
            ActiveModuleBundleState bundle,
            LauncherLoginResult login,
            string installationId,
            string apiEndpoint,
            DateTime utcNow,
            Guid launchId,
            string ipcScopeToken)
        {
            ValidateLaunchInputs(processPlan, bundle, login, installationId, apiEndpoint, ipcScopeToken);

            var issuedAtUtc = (utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime()).ToString("o", CultureInfo.InvariantCulture);
            var launchIdText = launchId.ToString("D");
            var common = new Dictionary<string, object>
            {
                { "schemaVersion", SessionContractVersion },
                { "launchId", launchIdText },
                { "ipcScopeToken", ipcScopeToken },
                { "issuedAtUtc", issuedAtUtc },
                { "launcherVersion", LauncherVersion.Current },
                { "installationId", installationId },
                { "channel", LauncherVersion.Channel }
            };

            var shell = new Dictionary<string, object>(common)
            {
                { "account", login.Account },
                { "characters", login.Characters }
            };
            if (ContainsShellForbiddenProperty(shell))
                throw new InvalidOperationException("Shell Launcher session에 Server 비밀 또는 API endpoint가 포함되어 있습니다.");

            var roleLevel = AccountNumber(login.Account, "roleLevel", RoleLevelFromLabel(AccountText(login.Account, "roleLabel", AccountText(login.Account, "role", "Member"))));
            var isMeterAdmin = AccountBool(login.Account, "meterAdmin") || roleLevel >= 5;
            var host = new Dictionary<string, object>(common)
            {
                { "sessionToken", login.SessionToken },
                { "apiEndpoint", apiEndpoint },
                { "isMeterAdmin", isMeterAdmin },
                { "diagnosticsAllowed", AccountBool(login.Account, "diagnosticsAllowed") || isMeterAdmin }
            };

            return new RuntimeLaunchSessionPlan
            {
                ProcessPlan = processPlan,
                LaunchId = launchIdText,
                IpcScopeToken = ipcScopeToken,
                IssuedAtUtc = issuedAtUtc,
                ShellSessionLine = EncodeSessionLine(ShellSessionPrefix, shell),
                EngineHostSessionLine = EncodeSessionLine(EngineHostSessionPrefix, host)
            };
        }

        private static async Task<SplitRuntimeLaunchResult> LaunchAsync(
            RuntimeLaunchSessionPlan session,
            IRuntimeProcessLauncher launcher,
            Func<TimeSpan, Task> delay)
        {
            if (session == null || session.ProcessPlan == null)
                throw new ArgumentNullException("session");
            if (launcher == null) throw new ArgumentNullException("launcher");
            if (delay == null) throw new ArgumentNullException("delay");

            IRuntimeChildProcess shell = null;
            IRuntimeChildProcess host = null;
            try
            {
                // Shell starts first and waits up to the READY deadline for the Host pipe.
                // If Shell cannot accept its secret-free session, no Host carrying the
                // Server session is created.
                shell = launcher.Start(StartSpec("shell", session.ProcessPlan.ShellExecutable, true));
                await shell.WriteSessionLineAsync(session.ShellSessionLine).ConfigureAwait(false);
                await delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
                RequireRunning(shell, "Shell");

                host = launcher.Start(StartSpec("engine-host", session.ProcessPlan.EngineHostExecutable, false));
                await host.WriteSessionLineAsync(session.EngineHostSessionLine).ConfigureAwait(false);
                await delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                RequireRunning(host, "EngineHost");
                RequireRunning(shell, "Shell");

                return new SplitRuntimeLaunchResult
                {
                    LaunchId = session.LaunchId,
                    ShellProcessId = shell.Id,
                    EngineHostProcessId = host.Id,
                    RuntimeBundleRevision = session.ProcessPlan.RuntimeBundleRevision,
                    RuntimeBundleLockSha256 = session.ProcessPlan.RuntimeBundleLockSha256
                };
            }
            finally
            {
                if (host != null) host.Dispose();
                if (shell != null) shell.Dispose();
            }
        }

        private static RuntimeProcessStartSpec StartSpec(string target, string executable, bool showWindow)
        {
            var fullPath = Path.GetFullPath(executable ?? "");
            if (!File.Exists(fullPath))
                throw new InvalidOperationException(target + " 실행 파일이 검증된 모듈 슬롯에 없습니다.");
            return new RuntimeProcessStartSpec
            {
                Target = target,
                Executable = fullPath,
                WorkingDirectory = Path.GetDirectoryName(fullPath),
                ShowWindow = showWindow,
                RedirectStandardInput = true,
                Arguments = ""
            };
        }

        private static void RequireRunning(IRuntimeChildProcess process, string target)
        {
            if (process == null) throw new InvalidOperationException(target + " 프로세스를 시작하지 못했습니다.");
            if (process.HasExited)
                throw new InvalidOperationException(target + " 프로세스가 Launcher session 전달 직후 종료되었습니다. 종료 코드: " + process.ExitCode);
        }

        private static void ValidateLaunchInputs(
            PrivateRuntimeProcessPlan processPlan,
            ActiveModuleBundleState bundle,
            LauncherLoginResult login,
            string installationId,
            string apiEndpoint,
            string ipcScopeToken)
        {
            if (processPlan == null || bundle == null)
                throw new InvalidOperationException("검증된 Runtime process plan과 active Bundle이 필요합니다.");
            if (!String.Equals(bundle.Status, ModuleBundleActivator.ActiveStatus, StringComparison.Ordinal) ||
                !bundle.ActivationAtomic ||
                !String.Equals(bundle.Channel, LauncherVersion.Channel, StringComparison.Ordinal) ||
                !String.Equals(bundle.BundleRevision, processPlan.RuntimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(bundle.BundleLockSha256, processPlan.RuntimeBundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(bundle.ModuleSetHash, processPlan.RuntimeModuleSetHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Runtime process plan이 검증된 active Bundle identity와 일치하지 않습니다.");
            if (login == null || String.IsNullOrWhiteSpace(login.SessionToken) ||
                login.SessionToken.Length < 20 || login.SessionToken.Length > 200)
                throw new InvalidOperationException("EngineHost에 전달할 Server session이 올바르지 않습니다.");
            if (login.Account == null || login.Characters == null || login.Characters.Count == 0)
                throw new InvalidOperationException("Shell에 전달할 계정·캐릭터 정보가 없습니다.");

            Guid parsed;
            if (!Guid.TryParse(installationId, out parsed))
                throw new InvalidOperationException("Launcher installation id가 올바르지 않습니다.");
            if (!IsScopeToken(ipcScopeToken))
                throw new InvalidOperationException("Launcher IPC scope token이 올바르지 않습니다.");

            Uri endpoint;
            if (!Uri.TryCreate(apiEndpoint, UriKind.Absolute, out endpoint) ||
                !String.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(endpoint.Host, ExpectedProjectHost, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(endpoint.AbsolutePath, "/functions/v1/" + LauncherBuildProfile.FunctionName, StringComparison.Ordinal))
                throw new InvalidOperationException("EngineHost API endpoint가 승인된 KINOJO Server endpoint와 일치하지 않습니다.");
        }

        private static string EncodeSessionLine(string prefix, Dictionary<string, object> payload)
        {
            var bytes = Utf8.GetBytes(Json.Serialize(payload));
            if (bytes.Length == 0 || bytes.Length > MaximumDecodedSessionBytes)
                throw new InvalidOperationException("Launcher session payload 크기가 허용 범위를 벗어났습니다.");
            var encoded = Convert.ToBase64String(bytes);
            if (encoded.Length == 0 || encoded.Length > MaximumEncodedSessionChars)
                throw new InvalidOperationException("Launcher session envelope 크기가 허용 범위를 벗어났습니다.");
            return prefix + encoded;
        }

        private static bool IsScopeToken(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length < 24 || value.Length > 96) return false;
            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (!((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') ||
                      (ch >= '0' && ch <= '9') || ch == '_' || ch == '-')) return false;
            }
            return true;
        }

        private static bool ContainsShellForbiddenProperty(object value)
        {
            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                foreach (DictionaryEntry item in dictionary)
                {
                    var key = Convert.ToString(item.Key) ?? "";
                    if (String.Equals(key, "sessionToken", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(key, "apiEndpoint", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(key, "passKey", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(key, "accessToken", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(key, "refreshToken", StringComparison.OrdinalIgnoreCase) ||
                        ContainsShellForbiddenProperty(item.Value)) return true;
                }
                return false;
            }
            if (value is string) return false;
            var sequence = value as IEnumerable;
            if (sequence != null)
                foreach (var item in sequence)
                    if (ContainsShellForbiddenProperty(item)) return true;
            return false;
        }

        private static string AccountText(Dictionary<string, object> account, string key, string fallback)
        {
            object value;
            return account != null && account.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : fallback;
        }

        private static bool AccountBool(Dictionary<string, object> account, string key)
        {
            bool value;
            return Boolean.TryParse(AccountText(account, key, ""), out value) && value;
        }

        private static int AccountNumber(Dictionary<string, object> account, string key, int fallback)
        {
            int value;
            return Int32.TryParse(AccountText(account, key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
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

    internal sealed class SystemRuntimeProcessLauncher : IRuntimeProcessLauncher
    {
        public IRuntimeChildProcess Start(RuntimeProcessStartSpec spec)
        {
            if (spec == null || !spec.RedirectStandardInput || !String.IsNullOrEmpty(spec.Arguments))
                throw new InvalidOperationException("Runtime process는 command-line 비밀 없이 redirected stdin으로 시작해야 합니다.");
            var start = new ProcessStartInfo(spec.Executable)
            {
                Arguments = "",
                WorkingDirectory = spec.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = !spec.ShowWindow,
                WindowStyle = spec.ShowWindow ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
                LoadUserProfile = false
            };
            var process = Process.Start(start);
            if (process == null) throw new InvalidOperationException(spec.Target + " 프로세스를 시작하지 못했습니다.");
            return new SystemRuntimeChildProcess(process);
        }
    }

    internal sealed class SystemRuntimeChildProcess : IRuntimeChildProcess
    {
        private readonly Process _process;

        internal SystemRuntimeChildProcess(Process process)
        {
            _process = process ?? throw new ArgumentNullException("process");
        }

        public int Id { get { return _process.Id; } }
        public bool HasExited { get { return _process.HasExited; } }
        public int ExitCode { get { return _process.HasExited ? _process.ExitCode : -1; } }

        public async Task WriteSessionLineAsync(string line)
        {
            if (String.IsNullOrWhiteSpace(line))
                throw new InvalidOperationException("Runtime Launcher session line이 비어 있습니다.");
            await _process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
            _process.StandardInput.Close();
        }

        public void Dispose()
        {
            try { _process.StandardInput.Close(); }
            catch { }
            _process.Dispose();
        }
    }
}
