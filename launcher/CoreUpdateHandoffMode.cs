using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal sealed class CoreUpdateHandoffRequest
    {
        public string RequestId { get; set; }
        public int CoreProcessId { get; set; }
    }

    internal sealed class CoreUpdateHandoffEnvelope
    {
        public string RequestId { get; set; }
        public int CoreProcessId { get; set; }
        public string SessionToken { get; set; }
        public string InstallationId { get; set; }
        public string CurrentCoreVersion { get; set; }
        public DateTime IssuedAtUtc { get; set; }
        public Dictionary<string, object> Account { get; set; }
        public List<Dictionary<string, object>> Characters { get; set; }
    }

    internal static class CoreUpdateHandoffProtocol
    {
        public const string ModeArgument = "--core-update-handoff";
        public const string RequestArgument = "--request-id";
        public const string ProcessArgument = "--core-pid";
        public const string EnvelopePrefix = "KINOJO_CORE_UPDATE_HANDOFF_V1 ";
        public const string ReadyPrefix = "KINOJO_LAUNCHER_READY_TO_TAKEOVER_V1 ";
        public const string RejectedPrefix = "KINOJO_LAUNCHER_HANDOFF_REJECTED_V1 ";

        private static readonly Regex SemanticVersion = new Regex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.Compiled);

        public static bool IsRequested(string[] args)
        {
            if (args == null) return false;
            foreach (var value in args)
            {
                if (String.Equals(value, ModeArgument, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public static bool TryParseArguments(string[] args, out CoreUpdateHandoffRequest request, out string error)
        {
            request = null;
            error = "INVALID_ARGUMENTS";
            if (args == null || args.Length != 5 ||
                !String.Equals(args[0], ModeArgument, StringComparison.Ordinal) ||
                !String.Equals(args[1], RequestArgument, StringComparison.Ordinal) ||
                !String.Equals(args[3], ProcessArgument, StringComparison.Ordinal)) return false;

            Guid requestId;
            int processId;
            if (!Guid.TryParseExact(args[2] ?? "", "N", out requestId) ||
                !Int32.TryParse(args[4], out processId) || processId <= 0) return false;

            request = new CoreUpdateHandoffRequest
            {
                RequestId = requestId.ToString("N"),
                CoreProcessId = processId
            };
            error = "";
            return true;
        }

        public static CoreUpdateHandoffEnvelope ReadEnvelope(CoreUpdateHandoffRequest request)
        {
            if (request == null) throw new InvalidOperationException("인계 요청이 없습니다.");
            if (!Console.IsInputRedirected) throw new InvalidOperationException("인계 입력 채널이 연결되지 않았습니다.");
            return ParseEnvelopeLine(Console.In.ReadLine(), request, DateTime.UtcNow);
        }

        internal static CoreUpdateHandoffEnvelope ParseEnvelopeLineForTest(string line, CoreUpdateHandoffRequest request, DateTime utcNow)
        {
            return ParseEnvelopeLine(line, request, utcNow);
        }

        private static CoreUpdateHandoffEnvelope ParseEnvelopeLine(string line, CoreUpdateHandoffRequest request, DateTime utcNow)
        {
            if (String.IsNullOrWhiteSpace(line) || !line.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("지원하지 않는 인계 Envelope입니다.");
            var encoded = line.Substring(EnvelopePrefix.Length).Trim();
            if (encoded.Length == 0 || encoded.Length > 2 * 1024 * 1024)
                throw new InvalidOperationException("인계 Envelope 크기가 허용 범위를 벗어났습니다.");

            Dictionary<string, object> raw;
            try
            {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                raw = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 }
                    .DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("인계 Envelope를 해석하지 못했습니다.", ex);
            }

            if (raw == null || Number(raw, "schemaVersion", 0) != 1)
                throw new InvalidOperationException("지원하지 않는 인계 Envelope 버전입니다.");
            var requestId = Text(raw, "requestId", "");
            var coreProcessId = Number(raw, "coreProcessId", 0);
            if (!String.Equals(requestId, request.RequestId, StringComparison.Ordinal) || coreProcessId != request.CoreProcessId)
                throw new InvalidOperationException("인계 요청 식별값이 일치하지 않습니다.");

            DateTime issuedAt;
            if (!DateTime.TryParse(Text(raw, "issuedAtUtc", ""), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out issuedAt) || Math.Abs((utcNow - issuedAt.ToUniversalTime()).TotalSeconds) > 30)
                throw new InvalidOperationException("인계 Envelope 유효 시간이 지났습니다.");

            var sessionToken = Text(raw, "sessionToken", "");
            var installationId = Text(raw, "installationId", "");
            var currentCoreVersion = Text(raw, "currentCoreVersion", "");
            Guid parsedInstallation;
            if (sessionToken.Length < 20 || sessionToken.Length > 200 ||
                !Guid.TryParse(installationId, out parsedInstallation) ||
                !SemanticVersion.IsMatch(currentCoreVersion))
                throw new InvalidOperationException("인계 인증 정보가 올바르지 않습니다.");

            var account = Dict(raw, "account") ?? new Dictionary<string, object>();
            var characters = DictList(raw, "characters");
            if (characters.Count == 0) throw new InvalidOperationException("인계할 캐릭터 정보가 없습니다.");

            return new CoreUpdateHandoffEnvelope
            {
                RequestId = requestId,
                CoreProcessId = coreProcessId,
                SessionToken = sessionToken,
                InstallationId = parsedInstallation.ToString("N"),
                CurrentCoreVersion = currentCoreVersion,
                IssuedAtUtc = issuedAt.ToUniversalTime(),
                Account = account,
                Characters = characters
            };
        }

        public static string ReadyLine(string requestId, string targetVersion)
        {
            return ReadyPrefix + (requestId ?? "") + " " + (targetVersion ?? "");
        }

        public static string RejectedLine(string requestId, string code)
        {
            return RejectedPrefix + (requestId ?? "") + " " + SanitizeCode(code);
        }

        public static int CompareVersions(string left, string right)
        {
            if (!SemanticVersion.IsMatch(left ?? "") || !SemanticVersion.IsMatch(right ?? ""))
                throw new InvalidOperationException("Core version 형식이 올바르지 않습니다.");
            var leftParts = left.Split('.').Select(Int32.Parse).ToArray();
            var rightParts = right.Split('.').Select(Int32.Parse).ToArray();
            for (var index = 0; index < 3; index++)
            {
                var compared = leftParts[index].CompareTo(rightParts[index]);
                if (compared != 0) return compared;
            }
            return 0;
        }

        private static string SanitizeCode(string value)
        {
            var code = Regex.Replace((value ?? "HANDOFF_FAILED").ToUpperInvariant(), @"[^A-Z0-9_]", "_");
            return String.IsNullOrWhiteSpace(code) ? "HANDOFF_FAILED" : code.Substring(0, Math.Min(64, code.Length));
        }

        private static string Text(Dictionary<string, object> source, string key, string fallback)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : fallback;
        }

        private static int Number(Dictionary<string, object> source, string key, int fallback)
        {
            int value;
            return Int32.TryParse(Text(source, key, ""), out value) ? value : fallback;
        }

        private static Dictionary<string, object> Dict(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static List<Dictionary<string, object>> DictList(Dictionary<string, object> source, string key)
        {
            var result = new List<Dictionary<string, object>>();
            object value;
            if (source == null || !source.TryGetValue(key, out value)) return result;
            var rows = value as IEnumerable;
            if (rows == null) return result;
            foreach (var row in rows)
            {
                var dictionary = row as Dictionary<string, object>;
                if (dictionary != null) result.Add(dictionary);
            }
            return result;
        }
    }

    internal static class CoreUpdateHandoffMode
    {
        private const int CoreExitTimeoutMilliseconds = 20000;

        public static async Task<int> RunAsync(CoreUpdateHandoffRequest request)
        {
            var readySent = false;
            try
            {
                var envelope = CoreUpdateHandoffProtocol.ReadEnvelope(request);
                using (var coreProcess = RequireRunningCoreProcess(request.CoreProcessId))
                using (var api = new LauncherApiClient())
                using (var installer = new CorePackageInstaller())
                using (var catalogInstaller = new CatalogPackInstaller())
                using (var uiAssetInstaller = new UiAssetPackInstaller())
                using (var privateRuntimeUpdater = new PrivateRuntimePackageUpdater())
                using (var captureUpdater = new CaptureModuleUpdater())
                using (var protocolUpdater = new ProtocolModuleUpdater())
                using (var combatEncounterUpdater = new CombatEncounterCompatibilityGroupUpdater())
                using (var combatUpdater = new CombatEncounterIndividualModuleUpdater("combat"))
                using (var encounterUpdater = new CombatEncounterIndividualModuleUpdater("encounter"))
                using (var syncUpdater = new SyncModuleUpdater())
                using (var shellUpdater = new ShellModuleUpdater())
                {
                    var previous = installer.ReadActiveState();
                    if (previous == null || !String.Equals(previous.CoreVersion, envelope.CurrentCoreVersion, StringComparison.Ordinal))
                        throw new InvalidOperationException("현재 활성 Core 상태가 인계 요청과 일치하지 않습니다.");

                    var authorization = await api.AuthorizeCoreUpdateAsync(
                        envelope.SessionToken,
                        envelope.InstallationId,
                        envelope.CurrentCoreVersion).ConfigureAwait(false);
                    if (authorization == null || !authorization.Authorized || authorization.Release == null)
                        throw new InvalidOperationException("Core 업데이트 승인을 받지 못했습니다.");
                    if (CoreUpdateHandoffProtocol.CompareVersions(authorization.Release.CoreVersion, envelope.CurrentCoreVersion) <= 0)
                        throw new InvalidOperationException("설치 가능한 새 Core가 없습니다.");
                    RequireLaunchEnabled(await api.GetLaunchOperationAsync().ConfigureAwait(false));

                    var catalogAuthorization = await api.AuthorizeCatalogPackUpdatesAsync(
                        envelope.SessionToken,
                        envelope.InstallationId,
                        CatalogPackUpdateCoordinator.CurrentStatePayload(catalogInstaller)).ConfigureAwait(false);
                    await CatalogPackUpdateCoordinator.ApplyAsync(
                        catalogInstaller,
                        catalogAuthorization,
                        api.ProjectHost,
                        CancellationToken.None).ConfigureAwait(false);

                    var uiAssetAuthorization = await api.AuthorizeUiAssetPackUpdateAsync(
                        envelope.SessionToken,
                        envelope.InstallationId,
                        UiAssetPackUpdateCoordinator.CurrentStatePayload(uiAssetInstaller)).ConfigureAwait(false);
                    await UiAssetPackUpdateCoordinator.ApplyAsync(
                        uiAssetInstaller,
                        uiAssetAuthorization,
                        api.ProjectHost,
                        CancellationToken.None).ConfigureAwait(false);

                    var privateRuntimeAuthorization = await api.AuthorizePrivateRuntimeUpdateAsync(
                        envelope.SessionToken,
                        envelope.InstallationId,
                        PrivateRuntimeUpdateCoordinator.CurrentStatePayload(privateRuntimeUpdater)).ConfigureAwait(false);
                    await PrivateRuntimeUpdateCoordinator.ApplyAsync(
                        privateRuntimeUpdater,
                        privateRuntimeAuthorization,
                        api.ProjectHost,
                        CancellationToken.None).ConfigureAwait(false);

                    var captureAuthorization = await api.AuthorizeCaptureModuleUpdateAsync(
                        envelope.SessionToken,
                        envelope.InstallationId,
                        CaptureModuleUpdateCoordinator.CurrentStatePayload(captureUpdater),
                        PrivateRuntimeUpdateCoordinator.CurrentStatePayload(privateRuntimeUpdater)).ConfigureAwait(false);
                    await CaptureModuleUpdateCoordinator.ApplyAsync(
                        captureUpdater,
                        captureAuthorization,
                        api.ProjectHost,
                        CancellationToken.None).ConfigureAwait(false);

                    var protocolAuthorization = await api.AuthorizeProtocolModuleUpdateAsync(
                        envelope.SessionToken,
                        envelope.InstallationId,
                        ProtocolModuleUpdateCoordinator.CurrentStatePayload(protocolUpdater),
                        CaptureModuleUpdateCoordinator.CurrentStatePayload(captureUpdater),
                        PrivateRuntimeUpdateCoordinator.CurrentStatePayload(privateRuntimeUpdater)).ConfigureAwait(false);
                    await ProtocolModuleUpdateCoordinator.ApplyAsync(
                        protocolUpdater,
                        protocolAuthorization,
                        api.ProjectHost,
                        CancellationToken.None).ConfigureAwait(false);

                    if (CombatEncounterCompatibilityGroupUpdateCoordinator.CurrentStatePayload(combatEncounterUpdater) == null)
                    {
                        var combatEncounterAuthorization = await api.AuthorizeCombatEncounterCompatibilityGroupUpdateAsync(
                            envelope.SessionToken,
                            envelope.InstallationId,
                            null,
                            ProtocolModuleUpdateCoordinator.CurrentStatePayload(protocolUpdater),
                            CaptureModuleUpdateCoordinator.CurrentStatePayload(captureUpdater),
                            PrivateRuntimeUpdateCoordinator.CurrentStatePayload(privateRuntimeUpdater)).ConfigureAwait(false);
                        await CombatEncounterCompatibilityGroupUpdateCoordinator.ApplyAsync(
                            combatEncounterUpdater,
                            combatEncounterAuthorization,
                            api.ProjectHost,
                            CancellationToken.None).ConfigureAwait(false);
                    }

                    var combatAuthorization = await api.AuthorizeCombatEncounterIndividualModuleUpdateAsync(
                        "combat",
                        envelope.SessionToken,
                        envelope.InstallationId,
                        CombatEncounterIndividualModuleUpdateCoordinator.CurrentStatePayload(combatUpdater),
                        CombatEncounterIndividualModuleUpdateCoordinator.CurrentStatePayload(encounterUpdater),
                        CombatEncounterCompatibilityGroupUpdateCoordinator.CurrentStatePayload(combatEncounterUpdater),
                        ProtocolModuleUpdateCoordinator.CurrentStatePayload(protocolUpdater),
                        CaptureModuleUpdateCoordinator.CurrentStatePayload(captureUpdater),
                        PrivateRuntimeUpdateCoordinator.CurrentStatePayload(privateRuntimeUpdater)).ConfigureAwait(false);
                    await CombatEncounterIndividualModuleUpdateCoordinator.ApplyAsync(
                        combatUpdater, combatAuthorization, api.ProjectHost, CancellationToken.None).ConfigureAwait(false);

                    var encounterAuthorization = await api.AuthorizeCombatEncounterIndividualModuleUpdateAsync(
                        "encounter",
                        envelope.SessionToken,
                        envelope.InstallationId,
                        CombatEncounterIndividualModuleUpdateCoordinator.CurrentStatePayload(encounterUpdater),
                        CombatEncounterIndividualModuleUpdateCoordinator.CurrentStatePayload(combatUpdater),
                        CombatEncounterCompatibilityGroupUpdateCoordinator.CurrentStatePayload(combatEncounterUpdater),
                        ProtocolModuleUpdateCoordinator.CurrentStatePayload(protocolUpdater),
                        CaptureModuleUpdateCoordinator.CurrentStatePayload(captureUpdater),
                        PrivateRuntimeUpdateCoordinator.CurrentStatePayload(privateRuntimeUpdater)).ConfigureAwait(false);
                    await CombatEncounterIndividualModuleUpdateCoordinator.ApplyAsync(
                        encounterUpdater, encounterAuthorization, api.ProjectHost, CancellationToken.None).ConfigureAwait(false);

                    var syncAuthorization = await api.AuthorizeSyncModuleUpdateAsync(
                        envelope.SessionToken,
                        envelope.InstallationId,
                        SyncModuleUpdateCoordinator.CurrentStatePayload(syncUpdater),
                        ProtocolModuleUpdateCoordinator.CurrentStatePayload(protocolUpdater),
                        CaptureModuleUpdateCoordinator.CurrentStatePayload(captureUpdater),
                        PrivateRuntimeUpdateCoordinator.CurrentStatePayload(privateRuntimeUpdater)).ConfigureAwait(false);
                    await SyncModuleUpdateCoordinator.ApplyAsync(
                        syncUpdater,
                        syncAuthorization,
                        api.ProjectHost,
                        CancellationToken.None).ConfigureAwait(false);

                    var shellAuthorization = await api.AuthorizeShellModuleUpdateAsync(
                        envelope.SessionToken,
                        envelope.InstallationId,
                        ShellModuleUpdateCoordinator.CurrentStatePayload(shellUpdater)).ConfigureAwait(false);
                    await ShellModuleUpdateCoordinator.ApplyAsync(
                        shellUpdater,
                        shellAuthorization,
                        api.ProjectHost,
                        CancellationToken.None).ConfigureAwait(false);

                    var login = new LauncherLoginResult
                    {
                        SessionToken = envelope.SessionToken,
                        DisplayName = AccountText(envelope.Account, "mainCharacterName", "KINOJO 사용자"),
                        Account = envelope.Account,
                        Characters = envelope.Characters
                    };

                    Console.Out.WriteLine(CoreUpdateHandoffProtocol.ReadyLine(request.RequestId, authorization.Release.CoreVersion));
                    Console.Out.Flush();
                    readySent = true;

                    if (!coreProcess.WaitForExit(CoreExitTimeoutMilliseconds))
                        throw new InvalidOperationException("기존 Core가 제한 시간 안에 종료되지 않았습니다.");

                    CoreInstallResult install = null;
                    try
                    {
                        install = await installer.EnsureInstalledAsync(
                            authorization.Release,
                            api.ProjectHost,
                            null,
                            CancellationToken.None).ConfigureAwait(false);
                        RequireLaunchEnabled(await api.GetLaunchOperationAsync().ConfigureAwait(false));
                        await installer.LaunchAndVerifyAsync(install, login, envelope.InstallationId).ConfigureAwait(false);
                        return 0;
                    }
                    catch
                    {
                        if (install == null && previous != null)
                        {
                            await installer.LaunchAndVerifyAsync(new CoreInstallResult
                            {
                                Active = previous,
                                Previous = previous,
                                Changed = false
                            }, login, envelope.InstallationId).ConfigureAwait(false);
                            return 0;
                        }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
                if (!readySent && Console.IsOutputRedirected)
                {
                    try
                    {
                        Console.Out.WriteLine(CoreUpdateHandoffProtocol.RejectedLine(
                            request == null ? "" : request.RequestId,
                            FailureCode(ex)));
                        Console.Out.Flush();
                    }
                    catch { }
                }
                return readySent ? 71 : 70;
            }
        }

        private static void RequireLaunchEnabled(MeterLaunchOperation operation)
        {
            if (operation != null && operation.Enabled) return;
            var message = operation == null ? "미터기 실행 운영 상태를 확인하지 못했습니다." : operation.Message;
            throw new InvalidOperationException("미터기 실행 차단 · " + message);
        }

        private static Process RequireRunningCoreProcess(int processId)
        {
            Process process;
            try { process = Process.GetProcessById(processId); }
            catch (Exception ex) { throw new InvalidOperationException("인계 대상 Core 프로세스를 찾지 못했습니다.", ex); }
            if (process.HasExited || !String.Equals(process.ProcessName, LauncherBuildProfile.CoreProcessName, StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                throw new InvalidOperationException("인계 대상 Core 프로세스가 올바르지 않습니다.");
            }
            return process;
        }

        private static string FailureCode(Exception error)
        {
            if (error == null) return "HANDOFF_FAILED";
            if (error.Message.IndexOf("실행 차단", StringComparison.OrdinalIgnoreCase) >= 0) return "LAUNCH_DISABLED";
            if (error.Message.IndexOf("승인", StringComparison.OrdinalIgnoreCase) >= 0) return "AUTHORIZATION_REJECTED";
            if (error.Message.IndexOf("새 Core", StringComparison.OrdinalIgnoreCase) >= 0) return "NO_UPDATE";
            if (error.Message.IndexOf("프로세스", StringComparison.OrdinalIgnoreCase) >= 0) return "CORE_PROCESS_INVALID";
            return "HANDOFF_FAILED";
        }

        private static string AccountText(Dictionary<string, object> account, string key, string fallback)
        {
            object value;
            return account != null && account.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : fallback;
        }
    }
}
