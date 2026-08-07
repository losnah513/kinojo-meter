from pathlib import Path
import json

form = Path('launcher/LauncherForm.cs')
text = form.read_text(encoding='utf-8')
old = '''                    var progress = new Progress<int>(value =>
                    {
                        var mapped = 24 + (int)Math.Round(Math.Max(0, Math.Min(100, value)) * 0.70D);
                        _progress.Value = Math.Max(0, Math.Min(94, mapped));
                        _progressText.Text = _progress.Value + "%";
                    });
                    _preparedCore = await installer.EnsureInstalledAsync(
                        authorization.Release,
                        api.ProjectHost,
                        progress,
                        _cancellation.Token);
                    _version.Text = "Core " + _preparedCore.Active.CoreVersion;
                    _sidebarCore.Text = "Core " + _preparedCore.Active.CoreVersion;
                    SetOperationState(
                        "미터기 준비 완료",
                        _preparedCore.Changed ? "최신 Core 업데이트와 무결성 검증을 완료했습니다." : "최신 Core와 파일 무결성을 확인했습니다.",
                        false,
                        100);
'''
new = '''                    var prepareProgressActive = true;
                    var progress = new Progress<int>(value =>
                    {
                        if (!prepareProgressActive || IsDisposed || Disposing) return;
                        var mapped = 24 + (int)Math.Round(Math.Max(0, Math.Min(100, value)) * 0.70D);
                        _progress.Value = Math.Max(0, Math.Min(94, mapped));
                        _progressText.Text = _progress.Value + "%";
                    });
                    try
                    {
                        _preparedCore = await installer.EnsureInstalledAsync(
                            authorization.Release,
                            api.ProjectHost,
                            progress,
                            _cancellation.Token);
                    }
                    finally
                    {
                        // Progress<T> posts callbacks asynchronously to the UI context. Once
                        // install/verification is complete, queued 94% callbacks must never
                        // overwrite the terminal 100% state below.
                        prepareProgressActive = false;
                    }
                    _version.Text = "Core " + _preparedCore.Active.CoreVersion;
                    _sidebarCore.Text = "Core " + _preparedCore.Active.CoreVersion;
                    SetOperationState(
                        _preparedCore.Changed ? "업데이트가 완료되었습니다" : "현재 최신 버전입니다",
                        _preparedCore.Changed ? "최신 Core 업데이트와 파일 무결성 검증을 완료했습니다." : "최신 Core이며 파일 무결성 검증까지 완료했습니다.",
                        false,
                        100);
'''
if old not in text:
    raise SystemExit('LauncherForm progress block not found')
form.write_text(text.replace(old, new, 1), encoding='utf-8')

for path_text, product, channel, artifact, note in [
    ('release/launcher-version.json', 'KINOJO Meter Launcher', 'stable', 'KINOJO_Meter_Launcher_1.1.1.exe', '진행률 완료 상태 100% 고정 · 현재 최신 버전/업데이트 완료 문구 명확화 · 기존 Launcher/Core 보안 검증 유지'),
    ('release/launcher-staging-version.json', 'KINOJO Meter Launcher STAGING', 'staging', 'KINOJO_Meter_Launcher_Staging_1.1.1.exe', '비공개 Windows E2E · 진행률 완료 상태 100% 고정 · 현재 최신 버전/업데이트 완료 문구 명확화 · 기존 전용 RSA Core 경계 유지')
]:
    path = Path(path_text)
    data = json.loads(path.read_text(encoding='utf-8'))
    data['version'] = '1.1.1'
    data['fileVersion'] = '1.1.1.0'
    data['artifactName'] = artifact
    data['releaseNote'] = note
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

test = Path('tests/KINOJO.Meter.Launcher.Tests/Program.cs')
test_text = test.read_text(encoding='utf-8')
needle = '        public const string Current = "1.1.0";'
if needle not in test_text:
    raise SystemExit('Launcher test version constant not found')
test.write_text(test_text.replace(needle, '        public const string Current = "1.1.1";', 1), encoding='utf-8')
