using System;
using System.Collections.Generic;

namespace KinojoMeterLauncher
{
    internal sealed class LauncherLoginResult
    {
        public string SessionToken { get; set; }
        public string DisplayName { get; set; }
        public Dictionary<string, object> Account { get; set; }
        public List<Dictionary<string, object>> Characters { get; set; }
    }

    internal sealed class LauncherContentItem
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Channel { get; set; }
        public bool Pinned { get; set; }
        public string Title { get; set; }
        public string Summary { get; set; }
        public DateTimeOffset PublishedAt { get; set; }
        public string Version { get; set; }
        public string Url { get; set; }
    }

    internal sealed class LauncherContentLoadResult
    {
        public List<LauncherContentItem> Items { get; set; }
        public bool Cached { get; set; }
        public string Status { get; set; }
    }

    internal sealed class CoreUpdateAuthorization
    {
        public bool Authorized { get; set; }
        public bool BlockedByOperation { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public CoreReleaseManifest Release { get; set; }
    }

    internal sealed class MeterLaunchOperation
    {
        public string Channel { get; set; }
        public bool Enabled { get; set; }
        public string Message { get; set; }
    }

    internal sealed class LauncherUpdateCheckResult
    {
        public bool ReleaseAvailable { get; set; }
        public bool UpdateAvailable { get; set; }
        public LauncherUpdateManifest Release { get; set; }
    }

    internal sealed class LauncherUpdateManifest
    {
        public int SchemaVersion { get; set; }
        public string Channel { get; set; }
        public string Version { get; set; }
        public string FileVersion { get; set; }
        public string MinimumVersion { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public string Sha256 { get; set; }
        public string DownloadUrl { get; set; }
        public bool Mandatory { get; set; }
        public string ReleaseNote { get; set; }
        public bool CodeSignatureRequired { get; set; }
        public string PublisherSubject { get; set; }
        public string TrustMode { get; set; }
        public bool SmartScreenWarningExpected { get; set; }
    }

    internal sealed class LauncherUpdateProgress
    {
        public int Percentage { get; set; }
        public string Stage { get; set; }
    }

    internal sealed class CoreReleaseManifest
    {
        public int SchemaVersion { get; set; }
        public string Channel { get; set; }
        public string CoreVersion { get; set; }
        public string MinimumCoreVersion { get; set; }
        public string MinimumLauncherVersion { get; set; }
        public string PackageId { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public string Sha256 { get; set; }
        public string InstallManifestSha256 { get; set; }
        public string DownloadUrl { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string EntryPoint { get; set; }
        public bool Mandatory { get; set; }
        public string ReleaseNote { get; set; }
        public bool CodeSignatureRequired { get; set; }
        public string PublisherSubject { get; set; }
        public string IntegrityMode { get; set; }
        public string SigningKeyId { get; set; }
        public string ManifestSignature { get; set; }
    }

    internal sealed class CoreInstallManifest
    {
        public int SchemaVersion { get; set; }
        public string CoreVersion { get; set; }
        public string EntryPoint { get; set; }
        public List<CoreInstallFile> Files { get; set; }
    }

    internal sealed class CoreInstallFile
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
    }

    internal sealed class ActiveCoreState
    {
        public int SchemaVersion { get; set; }
        public string Channel { get; set; }
        public string CoreVersion { get; set; }
        public string MinimumCoreVersion { get; set; }
        public string MinimumLauncherVersion { get; set; }
        public string PackageId { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public string EntryPoint { get; set; }
        public string InstalledPath { get; set; }
        public string ActivatedAtUtc { get; set; }
        public string PackageSha256 { get; set; }
        public string InstallManifestSha256 { get; set; }
        public bool Mandatory { get; set; }
        public string IntegrityMode { get; set; }
        public string SigningKeyId { get; set; }
        public string ManifestSignature { get; set; }
    }

    internal sealed class CoreInstallResult
    {
        public ActiveCoreState Active { get; set; }
        public ActiveCoreState Previous { get; set; }
        public bool Changed { get; set; }
    }

    internal sealed class CatalogPackUpdateAuthorization
    {
        public bool Authorized { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public List<CatalogPackReleaseManifest> Releases { get; set; }
    }

    internal sealed class CatalogPackReleaseManifest
    {
        public int SchemaVersion { get; set; }
        public string Channel { get; set; }
        public string PackId { get; set; }
        public string CatalogVersion { get; set; }
        public string MinimumLauncherVersion { get; set; }
        public string PackageId { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public string Sha256 { get; set; }
        public string InstallManifestSha256 { get; set; }
        public string CatalogSha256 { get; set; }
        public string DownloadUrl { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string IntegrityMode { get; set; }
        public string SigningKeyId { get; set; }
        public string ManifestSignature { get; set; }
        public string ReleaseNote { get; set; }
    }

    internal sealed class CatalogPackInstallManifest
    {
        public int SchemaVersion { get; set; }
        public string PackId { get; set; }
        public string CatalogVersion { get; set; }
        public List<CatalogPackInstallFile> Files { get; set; }
    }

    internal sealed class CatalogPackInstallFile
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
    }

    internal sealed class ActiveCatalogPackState
    {
        public int SchemaVersion { get; set; }
        public string Channel { get; set; }
        public string PackId { get; set; }
        public string CatalogVersion { get; set; }
        public string MinimumLauncherVersion { get; set; }
        public string PackageId { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public string InstalledPath { get; set; }
        public string ActivatedAtUtc { get; set; }
        public string PackageSha256 { get; set; }
        public string InstallManifestSha256 { get; set; }
        public string CatalogSha256 { get; set; }
        public string IntegrityMode { get; set; }
        public string SigningKeyId { get; set; }
        public string ManifestSignature { get; set; }
    }

    internal sealed class CatalogPackInstallResult
    {
        public ActiveCatalogPackState Active { get; set; }
        public ActiveCatalogPackState Previous { get; set; }
        public bool Changed { get; set; }
        public bool Downloaded { get; set; }
    }

    internal sealed class UiAssetReleaseManifest
    {
        public int SchemaVersion { get; set; }
        public string Channel { get; set; }
        public string PackId { get; set; }
        public string Version { get; set; }
        public string MinimumLauncherVersion { get; set; }
        public string PackageId { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public string Sha256 { get; set; }
        public string InstallManifestSha256 { get; set; }
        public string ThemeSha256 { get; set; }
        public string DownloadUrl { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string IntegrityMode { get; set; }
        public string SigningKeyId { get; set; }
        public string ManifestSignature { get; set; }
        public string ReleaseNote { get; set; }
    }

    internal sealed class UiAssetInstallManifest
    {
        public int SchemaVersion { get; set; }
        public string PackId { get; set; }
        public string Version { get; set; }
        public string ThemeId { get; set; }
        public List<UiAssetInstallFile> Files { get; set; }
    }

    internal sealed class UiAssetInstallFile
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
    }

    internal sealed class ActiveUiAssetState
    {
        public int SchemaVersion { get; set; }
        public string Channel { get; set; }
        public string PackId { get; set; }
        public string Version { get; set; }
        public string MinimumLauncherVersion { get; set; }
        public string PackageId { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public string ThemeId { get; set; }
        public string InstalledPath { get; set; }
        public string ActivatedAtUtc { get; set; }
        public string PackageSha256 { get; set; }
        public string InstallManifestSha256 { get; set; }
        public string ThemeSha256 { get; set; }
        public string IntegrityMode { get; set; }
        public string SigningKeyId { get; set; }
        public string ManifestSignature { get; set; }
    }

    internal sealed class UiAssetInstallResult
    {
        public ActiveUiAssetState Active { get; set; }
        public ActiveUiAssetState Previous { get; set; }
        public bool Changed { get; set; }
        public bool Downloaded { get; set; }
    }

    internal sealed class UiAssetPackUpdateAuthorization
    {
        public bool Authorized { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public UiAssetReleaseManifest Release { get; set; }
    }
}
