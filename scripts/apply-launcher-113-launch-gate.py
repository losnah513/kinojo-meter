from pathlib import Path
import json


def read(path):
    return Path(path).read_text(encoding='utf-8')


def write(path, text):
    Path(path).write_text(text, encoding='utf-8', newline='\n')


def replace_once(path, old, new, label):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{label}: expected exactly one match, found {count}')
    write(path, text.replace(old, new, 1))


replace_once(
    'launcher/LauncherModels.cs',
    '''    internal sealed class CoreUpdateAuthorization\n    {\n        public bool Authorized { get; set; }\n        public bool BlockedByOperation { get; set; }\n        public string Code { get; set; }\n        public string Message { get; set; }\n        public CoreReleaseManifest Release { get; set; }\n    }\n\n''',
    '''    internal sealed class CoreUpdateAuthorization\n    {\n        public bool Authorized { get; set; }\n        public bool BlockedByOperation { get; set; }\n        public string Code { get; set; }\n        public string Message { get; set; }\n        public CoreReleaseManifest Release { get; set; }\n    }\n\n    internal sealed class MeterLaunchOperation\n    {\n        public string Channel { get; set; }\n        public bool Enabled { get; set; }\n        public string Message { get; set; }\n    }\n\n''',
    'launch operation model')

replace_once(
    'launcher/LauncherApiClient.cs',
    '''        public async Task<LauncherUpdateCheckResult> CheckLauncherUpdateAsync()\n        {\n            var result = await PostAsync(new Dictionary<string, object>\n            {\n                { "action", "distributionManifest" },\n                { "channel", LauncherVersion.Channel },\n                { "launcherVersion", LauncherVersion.Current }\n            }).ConfigureAwait(false);\n            if (!Bool(result, "ok"))\n                throw new InvalidOperationException(Text(result, "message", "Launcher 업데이트 정보를 확인하지 못했습니다."));\n            return ParseLauncherUpdate(result);\n        }\n\n''',
    '''        public async Task<LauncherUpdateCheckResult> CheckLauncherUpdateAsync()\n        {\n            var result = await PostAsync(new Dictionary<string, object>\n            {\n                { "action", "distributionManifest" },\n                { "channel", LauncherVersion.Channel },\n                { "launcherVersion", LauncherVersion.Current }\n            }).ConfigureAwait(false);\n            if (!Bool(result, "ok"))\n                throw new InvalidOperationException(Text(result, "message", "Launcher 업데이트 정보를 확인하지 못했습니다."));\n            return ParseLauncherUpdate(result);\n        }\n\n        public async Task<MeterLaunchOperation> GetLaunchOperationAsync()\n        {\n            var result = await PostAsync(new Dictionary<string, object>\n            {\n                { "action", "distributionManifest" },\n                { "channel", LauncherVersion.Channel },\n                { "launcherVersion", LauncherVersion.Current }\n            }).ConfigureAwait(false);\n            if (!Bool(result, "ok"))\n                throw new InvalidOperationException(Text(result, "message", "미터기 실행 운영 상태를 확인하지 못했습니다."));\n            return ParseLaunchOperation(result);\n        }\n\n''',
    'launch operation fetch')

replace_once(
    'launcher/LauncherApiClient.cs',
    '''        internal static LauncherUpdateCheckResult ParseLauncherUpdateForTest(Dictionary<string, object> value)\n        {\n            return ParseLauncherUpdate(value);\n        }\n\n''',
    '''        internal static LauncherUpdateCheckResult ParseLauncherUpdateForTest(Dictionary<string, object> value)\n        {\n            return ParseLauncherUpdate(value);\n        }\n\n        internal static MeterLaunchOperation ParseLaunchOperationForTest(Dictionary<string, object> value)\n        {\n            return ParseLaunchOperation(value);\n        }\n\n        private static MeterLaunchOperation ParseLaunchOperation(Dictionary<string, object> value)\n        {\n            var operation = Dict(value, "operation");\n            if (operation == null)\n            {\n                return new MeterLaunchOperation\n                {\n                    Channel = LauncherVersion.Channel,\n                    Enabled = false,\n                    Message = "미터기 실행 운영 상태를 확인하고 있습니다. 잠시 후 다시 시도해 주세요."\n                };\n            }\n            var message = Text(operation, "launchMessage", "").Trim();\n            return new MeterLaunchOperation\n            {\n                Channel = Text(operation, "channel", LauncherVersion.Channel),\n                Enabled = Bool(operation, "launchEnabled"),\n                Message = String.IsNullOrWhiteSpace(message)\n                    ? "키노조 미터 실행이 일시 중지되어 있습니다. 잠시 후 다시 시도해 주세요."\n                    : message\n            };\n        }\n\n''',
    'launch operation parser')

replace_once(
    'launcher/LauncherForm.cs',
    '''        private CoreInstallResult _preparedCore;\n        private string _installationId;\n        private bool _operationBusy;''',
    '''        private CoreInstallResult _preparedCore;\n        private string _installationId;\n        private MeterLaunchOperation _launchOperation;\n        private bool _operationBusy;''',
    'launcher form launch state')

replace_once(
    'launcher/LauncherForm.cs',
    '''            Shown += async delegate\n            {\n                var contentTask = LoadContentAsync();\n                await PrepareCoreAsync();\n                await contentTask;\n            };''',
    '''            Shown += async delegate\n            {\n                var contentTask = LoadContentAsync();\n                await PrepareCoreAsync();\n                await RefreshLaunchOperationAsync(true);\n                await contentTask;\n            };''',
    'initial launch gate refresh')

replace_once(
    'launcher/LauncherForm.cs',
    '''            _operationBusy = true;\n            _start.Enabled = false;\n            _start.Text = "KINOJO Meter 실행 중...";\n            _terms.Enabled = false;\n            _progress.Error = false;\n            try\n            {\n                using (var installer = new CorePackageInstaller())\n                {\n                    SetOperationState("미터기 실행 확인 중", "Core 실행과 준비 신호를 확인하고 있습니다.", false, 92);''',
    '''            _operationBusy = true;\n            _start.Enabled = false;\n            _start.Text = "KINOJO Meter 실행 중...";\n            _terms.Enabled = false;\n            _progress.Error = false;\n            try\n            {\n                if (!await RefreshLaunchOperationAsync(false)) return;\n                using (var installer = new CorePackageInstaller())\n                {\n                    SetOperationState("미터기 실행 확인 중", "Server 실행 허용 상태와 Core 준비 신호를 확인했습니다.", false, 92);''',
    'pre-launch server gate')

replace_once(
    'launcher/LauncherForm.cs',
    '''        private void RefreshStartButton()\n        {\n            if (_start == null) return;\n            _start.Enabled = _terms.Checked && !_operationBusy && !String.IsNullOrWhiteSpace(_login.SessionToken);\n        }\n\n''',
    '''        private async Task<bool> RefreshLaunchOperationAsync(bool showAllowedState)\n        {\n            try\n            {\n                using (var api = new LauncherApiClient())\n                    _launchOperation = await api.GetLaunchOperationAsync();\n                if (_launchOperation == null || !_launchOperation.Enabled)\n                {\n                    var message = _launchOperation == null ? "미터기 실행 운영 상태를 확인하지 못했습니다." : _launchOperation.Message;\n                    SetOperationState("미터기 실행 중지", message, false, _progress.Value);\n                    return false;\n                }\n                if (showAllowedState && _preparedCore != null)\n                    SetOperationState("실행 준비 완료", "현재 미터기 실행이 허용되어 있습니다.", false, 100);\n                return true;\n            }\n            catch (Exception error)\n            {\n                _launchOperation = new MeterLaunchOperation\n                {\n                    Channel = LauncherVersion.Channel,\n                    Enabled = false,\n                    Message = error.Message\n                };\n                SetOperationState("실행 상태 확인 필요", error.Message, true, _progress.Value);\n                return false;\n            }\n            finally\n            {\n                if (!IsDisposed) RefreshStartButton();\n            }\n        }\n\n        private void RefreshStartButton()\n        {\n            if (_start == null) return;\n            _start.Enabled = _terms.Checked && !_operationBusy && !String.IsNullOrWhiteSpace(_login.SessionToken) &&\n                _launchOperation != null && _launchOperation.Enabled;\n        }\n\n''',
    'launch gate button contract')

replace_once(
    'launcher/CoreUpdateHandoffMode.cs',
    '''                    if (CoreUpdateHandoffProtocol.CompareVersions(authorization.Release.CoreVersion, envelope.CurrentCoreVersion) <= 0)\n                        throw new InvalidOperationException("설치 가능한 새 Core가 없습니다.");\n\n                    var login = new LauncherLoginResult''',
    '''                    if (CoreUpdateHandoffProtocol.CompareVersions(authorization.Release.CoreVersion, envelope.CurrentCoreVersion) <= 0)\n                        throw new InvalidOperationException("설치 가능한 새 Core가 없습니다.");\n                    RequireLaunchEnabled(await api.GetLaunchOperationAsync().ConfigureAwait(false));\n\n                    var login = new LauncherLoginResult''',
    'handoff pre-ready launch gate')

replace_once(
    'launcher/CoreUpdateHandoffMode.cs',
    '''                        install = await installer.EnsureInstalledAsync(\n                            authorization.Release,\n                            api.ProjectHost,\n                            null,\n                            CancellationToken.None).ConfigureAwait(false);\n                        await installer.LaunchAndVerifyAsync(install, login, envelope.InstallationId).ConfigureAwait(false);''',
    '''                        install = await installer.EnsureInstalledAsync(\n                            authorization.Release,\n                            api.ProjectHost,\n                            null,\n                            CancellationToken.None).ConfigureAwait(false);\n                        RequireLaunchEnabled(await api.GetLaunchOperationAsync().ConfigureAwait(false));\n                        await installer.LaunchAndVerifyAsync(install, login, envelope.InstallationId).ConfigureAwait(false);''',
    'handoff final launch gate')

replace_once(
    'launcher/CoreUpdateHandoffMode.cs',
    '''        private static Process RequireRunningCoreProcess(int processId)\n        {''',
    '''        private static void RequireLaunchEnabled(MeterLaunchOperation operation)\n        {\n            if (operation != null && operation.Enabled) return;\n            var message = operation == null ? "미터기 실행 운영 상태를 확인하지 못했습니다." : operation.Message;\n            throw new InvalidOperationException("미터기 실행 차단 · " + message);\n        }\n\n        private static Process RequireRunningCoreProcess(int processId)\n        {''',
    'handoff launch helper')

replace_once(
    'launcher/CoreUpdateHandoffMode.cs',
    '''            if (error.Message.IndexOf("승인", StringComparison.OrdinalIgnoreCase) >= 0) return "AUTHORIZATION_REJECTED";\n            if (error.Message.IndexOf("새 Core", StringComparison.OrdinalIgnoreCase) >= 0) return "NO_UPDATE";''',
    '''            if (error.Message.IndexOf("실행 차단", StringComparison.OrdinalIgnoreCase) >= 0) return "LAUNCH_DISABLED";\n            if (error.Message.IndexOf("승인", StringComparison.OrdinalIgnoreCase) >= 0) return "AUTHORIZATION_REJECTED";\n            if (error.Message.IndexOf("새 Core", StringComparison.OrdinalIgnoreCase) >= 0) return "NO_UPDATE";''',
    'handoff failure code')

replace_once(
    'tests/KINOJO.Meter.Launcher.Tests/Program.cs',
    '''                Run("channel profile is compile-time bound", VerifyChannelProfile);\n                Run("parse hidden Core update handoff arguments", VerifyCoreUpdateHandoffArguments);''',
    '''                Run("channel profile is compile-time bound", VerifyChannelProfile);\n                Run("parse Server Meter launch operation", VerifyMeterLaunchOperationParsing);\n                Run("parse hidden Core update handoff arguments", VerifyCoreUpdateHandoffArguments);''',
    'launcher launch-gate test registration')

helper = r'''        private static void VerifyMeterLaunchOperationParsing()
        {
            var allowed = LauncherApiClient.ParseLaunchOperationForTest(new Dictionary<string, object>
            {
                { "ok", true },
                { "operation", new Dictionary<string, object>
                    {
                        { "channel", LauncherVersion.Channel },
                        { "launchEnabled", true },
                        { "launchMessage", "테스트 실행 허용" }
                    }
                }
            });
            if (allowed == null || !allowed.Enabled || allowed.Channel != LauncherVersion.Channel || allowed.Message != "테스트 실행 허용")
                throw new InvalidOperationException("Server Meter launch operation was not parsed.");

            var blocked = LauncherApiClient.ParseLaunchOperationForTest(new Dictionary<string, object>
            {
                { "ok", true },
                { "operation", new Dictionary<string, object>
                    {
                        { "channel", LauncherVersion.Channel },
                        { "launchEnabled", false },
                        { "launchMessage", "점검 중" }
                    }
                }
            });
            if (blocked == null || blocked.Enabled || blocked.Message != "점검 중")
                throw new InvalidOperationException("Server launch-disabled operation was not fail-closed.");

            var missing = LauncherApiClient.ParseLaunchOperationForTest(new Dictionary<string, object> { { "ok", true } });
            if (missing == null || missing.Enabled)
                throw new InvalidOperationException("Missing launch operation did not fail closed.");
        }

'''
replace_once(
    'tests/KINOJO.Meter.Launcher.Tests/Program.cs',
    '        private static void VerifyCoreUpdateHandoffArguments()\n',
    helper + '        private static void VerifyCoreUpdateHandoffArguments()\n',
    'launcher launch-gate test helper')

manifest_path = Path('release/launcher-staging-version.json')
manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
manifest['version'] = '1.1.3'
manifest['fileVersion'] = '1.1.3.0'
manifest['mandatory'] = True
manifest['releaseNote'] = 'Server 미터기 실행 ON/OFF Gate · Launcher 로그인/Core 업데이트 유지 · 실행 직전 재확인 · hidden Core update handoff 우회 차단'
manifest['artifactName'] = 'KINOJO_Meter_Launcher_Staging_1.1.3.exe'
manifest['cutoverState'] = 'STAGING_E2E'
manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

print('Staging Launcher 1.1.3 launch-gate patch applied.')
