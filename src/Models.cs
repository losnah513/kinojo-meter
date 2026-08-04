using System;
using System.Collections.Generic;

namespace KinojoMeterPrototype
{
    internal sealed class CharacterProfile
    {
        public string CharacterKey { get; set; }
        public string CharacterName { get; set; }
        public string MainCharacterName { get; set; }
        public string ServerId { get; set; }
        public string ServerName { get; set; }
        public string ClassKey { get; set; }
        public string ClassName { get; set; }
        public string CharKey { get; set; }
        public string ProfileImageUrl { get; set; }
        public string DetailUrl { get; set; }
        public long PveCombatPower { get; set; }
        public bool IsMain { get; set; }
        public override string ToString() { return CharacterName + " · " + ServerName + " · " + ClassName + (IsMain ? " · 본캐" : " · 부캐"); }
    }

    internal sealed class LoginResult
    {
        public string SessionToken { get; set; }
        public string MainCharacterName { get; set; }
        public string RoleLabel { get; set; }
        public int RoleLevel { get; set; }
        public bool IsMeterAdmin { get; set; }
        public bool DiagnosticsAllowed { get; set; }
        public bool IsPreview { get; set; }
        public List<CharacterProfile> Characters { get; set; }
    }

    internal sealed class DetectedPartyMember
    {
        public string EntityId { get; set; }
        public int Slot { get; set; }
        public int ServerRaw { get; set; }
        public string ServerName { get; set; }
        public string CharacterName { get; set; }
        public int ClassRaw { get; set; }
        public int Level { get; set; }
    }

    internal sealed class PartyRosterDetectedEventArgs : EventArgs
    {
        public string ConnectionKey { get; set; }
        public string Direction { get; set; }
        public bool LateAttached { get; set; }
        public string Evidence { get; set; }
        public List<DetectedPartyMember> Members { get; set; } = new List<DetectedPartyMember>();
    }

    internal sealed class GameHudObservation : EventArgs
    {
        public DateTime ObservedAtUtc { get; set; }
        public string CharacterName { get; set; }
        public string DungeonName { get; set; }
        public string DifficultyName { get; set; }
        public string Evidence { get; set; }
        public List<DetectedPartyMember> PartyMembers { get; set; } = new List<DetectedPartyMember>();
        public Dictionary<string, string> PartyClassColors { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> PartyServers { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class MeterCatalog
    {
        public string CatalogVersion { get; set; }
        public string DatabaseContract { get; set; }
        public MeterUpdateManifest DesktopUpdate { get; set; }
        public List<CatalogContent> Contents { get; set; } = new List<CatalogContent>();
        public List<CatalogDungeon> Dungeons { get; set; } = new List<CatalogDungeon>();
        public List<CatalogDifficulty> Difficulties { get; set; } = new List<CatalogDifficulty>();
        public List<CatalogVariant> Variants { get; set; } = new List<CatalogVariant>();
        public List<CatalogBoss> Bosses { get; set; } = new List<CatalogBoss>();
        public List<CatalogVariantBoss> VariantBosses { get; set; } = new List<CatalogVariantBoss>();
    }
    internal sealed class CatalogContent { public string ContentKey { get; set; } public string DisplayName { get; set; } public string ShortName { get; set; } public int PartySize { get; set; } public int DisplayOrder { get; set; } }
    internal sealed class CatalogDungeon { public string ContentKey { get; set; } public string DungeonKey { get; set; } public string DungeonName { get; set; } public int Tier { get; set; } public int OrderInTier { get; set; } public int PartySize { get; set; } }
    internal sealed class CatalogDifficulty { public string ContentKey { get; set; } public string DifficultyKey { get; set; } public string DisplayName { get; set; } public int DisplayOrder { get; set; } }
    internal sealed class CatalogVariant { public string ContentKey { get; set; } public string DungeonKey { get; set; } public string DifficultyKey { get; set; } public string VariantKey { get; set; } public int Tier { get; set; } public string DifficultyName { get; set; } }
    internal sealed class CatalogBoss { public string DungeonKey { get; set; } public string BossKey { get; set; } public string BossName { get; set; } public int BossOrder { get; set; } }
    internal sealed class CatalogVariantBoss { public string VariantKey { get; set; } public string BossKey { get; set; } public int BossOrder { get; set; } }


    internal sealed class MeterUpdateCheckResult
    {
        public bool ReleaseAvailable { get; set; }
        public bool UpdateAvailable { get; set; }
        public bool ClientVersionValid { get; set; }
        public string Channel { get; set; }
        public MeterUpdateManifest DesktopUpdate { get; set; }
    }

    internal sealed class MeterUpdateManifest
    {
        public string Version { get; set; }
        public string FileVersion { get; set; }
        public string MinimumVersion { get; set; }
        public string FileName { get; set; }
        public string DownloadUrl { get; set; }
        public string Sha256 { get; set; }
        public long FileSize { get; set; }
        public bool Mandatory { get; set; }
        public bool ReleaseMandatory { get; set; }
        public string ReleaseNote { get; set; }
        public string PublishedAt { get; set; }
        public string Channel { get; set; }
    }

    internal sealed class EncounterCatalogContext
    {
        public string CatalogVersion { get; set; }
        public string ContentKey { get; set; }
        public string ContentName { get; set; }
        public string DungeonKey { get; set; }
        public string DungeonName { get; set; }
        public string DifficultyKey { get; set; }
        public string DifficultyName { get; set; }
        public string VariantKey { get; set; }
        public int PartySize { get; set; }
    }

    internal sealed class CanonicalCatalogSelection
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public string ReasonCode { get; set; }
        public string CatalogVersion { get; set; }
        public string ClassKey { get; set; }
        public string ContentKey { get; set; }
        public string ContentName { get; set; }
        public string DungeonKey { get; set; }
        public string DungeonName { get; set; }
        public string DifficultyKey { get; set; }
        public string DifficultyName { get; set; }
        public string VariantKey { get; set; }
        public string BossKey { get; set; }
        public string BossName { get; set; }
    }

    internal sealed class CombatRow
    {
        public string ParticipantKey { get; set; }
        public string PlatformCharacterId { get; set; }
        public int PartyNumber { get; set; }
        public int PartySlot { get; set; }
        public string Name { get; set; }
        public string ServerId { get; set; }
        public string ServerName { get; set; }
        public int ServerRaw { get; set; }
        public string ClassKey { get; set; }
        public string ClassName { get; set; }
        public int ClassRaw { get; set; }
        public string ProfileImageUrl { get; set; }
        public long CombatPower { get; set; }
        public long ItemLevel { get; set; }
        public long TotalDamage { get; set; }
        public long Dps { get; set; }
        public double Share { get; set; }
        public bool IsSelf { get; set; }
        public bool IsEmpty { get; set; }
    }

    internal sealed class PartyProfileResult
    {
        public bool Ok { get; set; }
        public string ReasonCode { get; set; }
        public string Message { get; set; }
        public string ParticipantKey { get; set; }
        public long MeterCharacterId { get; set; }
        public string PlatformCharacterId { get; set; }
        public string ServerId { get; set; }
        public string ServerName { get; set; }
        public string CharacterName { get; set; }
        public string ClassKey { get; set; }
        public string ClassName { get; set; }
        public string ProfileImageUrl { get; set; }
        public long PveCombatPower { get; set; }
        public long PvpCombatPower { get; set; }
        public long ItemLevel { get; set; }
        public string ProfileStatus { get; set; }
        public string ProfileRefreshStatus { get; set; }
    }

    internal enum CombatEventKind
    {
        LocalPlayer,
        PartyMember,
        EntityIdentity,
        ZoneEntered,
        DungeonDetected,
        BossSpawn,
        BossHp,
        BossReset,
        Damage,
        EncounterEnd
    }

    internal sealed class CombatEvent
    {
        public CombatEventKind Kind { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string ActorId { get; set; }
        public long ActorRuntimeId { get; set; }
        public string PlatformCharacterId { get; set; }
        public string ActorName { get; set; }
        public string ActorServerId { get; set; }
        public string ActorServer { get; set; }
        public int ActorServerRaw { get; set; }
        public string ActorClassKey { get; set; }
        public string ActorClass { get; set; }
        public int ActorClassRaw { get; set; }
        public string ProfileImageUrl { get; set; }
        public string TargetId { get; set; }
        public long TargetRuntimeId { get; set; }
        public string TargetName { get; set; }
        public long Damage { get; set; }
        public long ActionId { get; set; }
        public long SkillId { get; set; }
        public long HitSequence { get; set; }
        public long CurrentHp { get; set; }
        public long MaxHp { get; set; }
        public int BossOrder { get; set; }
        public string BossIdentityMode { get; set; }
        public bool IsBoss { get; set; }
        public bool IsDot { get; set; }
        public int PartyNumber { get; set; }
        public int PartySlot { get; set; }
        public long CombatPower { get; set; }
        public long ItemLevel { get; set; }
        public string ContentKey { get; set; }
        public string ContentName { get; set; }
        public string DungeonKey { get; set; }
        public string DungeonName { get; set; }
        public string DifficultyKey { get; set; }
        public string DifficultyName { get; set; }
        public string VariantKey { get; set; }
        public string ZoneId { get; set; }
        public string ZoneName { get; set; }
    }

    internal sealed class CaptureRuntimeInfo
    {
        public string CaptureEngine { get; set; }
        public string CaptureMode { get; set; }
        public string DecoderType { get; set; }
        public string DecoderVersion { get; set; }
        public bool DecoderValidated { get; set; }
        public bool UploadEligible { get; set; }
        public string FlowKey { get; set; }
    }

    internal sealed class CombatSnapshot
    {
        public DateTime StartedAtUtc { get; set; }
        public DateTime LastEventUtc { get; set; }
        public string BossName { get; set; }
        public string BossId { get; set; }
        public long BossRuntimeId { get; set; }
        public long BossCurrentHp { get; set; }
        public long BossMaxHp { get; set; }
        public int BossOrder { get; set; }
        public string BossIdentityMode { get; set; }
        public string BossHpSource { get; set; }
        public string CompletionMode { get; set; }
        public bool BossConfirmed { get; set; }
        public bool IsRunning { get; set; }
        public bool IsCleared { get; set; }
        public string ContentKey { get; set; }
        public string ContentName { get; set; }
        public string DungeonKey { get; set; }
        public string DungeonName { get; set; }
        public string DifficultyKey { get; set; }
        public string DifficultyName { get; set; }
        public string VariantKey { get; set; }
        public string ZoneId { get; set; }
        public string ZoneName { get; set; }
        public string CaptureEngine { get; set; }
        public string CaptureMode { get; set; }
        public string DecoderType { get; set; }
        public string DecoderVersion { get; set; }
        public bool DecoderValidated { get; set; }
        public bool UploadEligible { get; set; }
        public List<CombatRow> Rows { get; set; } = new List<CombatRow>();
    }

    internal sealed class MeterPreferences
    {
        public double OverlayLeft { get; set; }
        public double OverlayTop { get; set; }
        public double OverlayWidth { get; set; }
        public int OverlayLayoutVersion { get; set; }
        public double BackgroundOpacity { get; set; }
        public double UiScale { get; set; }
        public bool Locked { get; set; }
        public bool ClickThrough { get; set; }
        public int GroupSize { get; set; }
        public static MeterPreferences Default() { return new MeterPreferences { OverlayLeft = 80, OverlayTop = 80, OverlayWidth = 360, OverlayLayoutVersion = 2, BackgroundOpacity = 0.86, UiScale = 1.0, Locked = false, ClickThrough = false, GroupSize = 5 }; }
    }

    internal sealed class MeterApiException : Exception
    {
        public string Code { get; private set; }
        public MeterApiException(string code, string message) : base(message) { Code = code; }
        public MeterApiException(string code, string message, Exception inner) : base(message, inner) { Code = code; }
    }
}
