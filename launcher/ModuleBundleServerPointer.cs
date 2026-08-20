using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace KinojoMeterLauncher
{
    internal sealed class ModuleBundlePointerContext
    {
        public string ServingChannel { get; set; }
        public string BundleOriginChannel { get; set; }
        public string BundleRevision { get; set; }
        public string BundleLockSha256 { get; set; }
        public long PointerGeneration { get; set; }
        public ModuleBundlePromotionAuthorization Promotion { get; set; }
        public ModuleBundlePointerRollbackAuthorization Rollback { get; set; }
    }

    internal sealed class ModuleBundlePromotionAuthorization
    {
        public int SchemaVersion { get; set; }
        public string PromotionId { get; set; }
        public string SourceChannel { get; set; }
        public string TargetChannel { get; set; }
        public string BundleRevision { get; set; }
        public string BundleLockSha256 { get; set; }
        public string StagingVerificationId { get; set; }
        public string PreviousStableBundleRevision { get; set; }
        public string PreviousStableBundleLockSha256 { get; set; }
        public long StablePointerGeneration { get; set; }
        public string PromotedAtUtc { get; set; }
    }

    internal sealed class ModuleBundlePointerRollbackAuthorization
    {
        public int SchemaVersion { get; set; }
        public string RollbackId { get; set; }
        public string SourcePromotionId { get; set; }
        public string TargetChannel { get; set; }
        public string BundleRevision { get; set; }
        public string BundleLockSha256 { get; set; }
        public string ReplacedBundleRevision { get; set; }
        public string ReplacedBundleLockSha256 { get; set; }
        public long StablePointerGeneration { get; set; }
        public string RolledBackAtUtc { get; set; }
    }

    internal static class ModuleBundleServerPointer
    {
        // The caller must populate this context only from an already verified, signed
        // Server Bundle Manifest. This class binds that authenticated envelope to the
        // exact immutable Bundle Lock consumed by the activator.
        public const string StablePromotionRequiredCode = "STABLE_PROMOTION_REQUIRED";
        public const string StablePromotionMismatchCode = "STABLE_PROMOTION_MISMATCH";
        public const string StableRollbackMismatchCode = "STABLE_ROLLBACK_MISMATCH";

        private static readonly Regex BundleRevisionPattern = new Regex("^B[0-9]{6}$", RegexOptions.CultureInvariant);
        private static readonly Regex Sha256Pattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

        public static string ExpectedOriginChannel(ModuleBundlePointerContext context, string servingChannel)
        {
            ValidateChannel(servingChannel, "Server Bundle serving channel");
            if (context == null) return servingChannel;
            ValidateChannel(context.ServingChannel, "Server Bundle pointer serving channel");
            ValidateChannel(context.BundleOriginChannel, "Server Bundle Lock origin channel");
            if (!String.Equals(context.ServingChannel, servingChannel, StringComparison.Ordinal))
                throw new InvalidOperationException("Server Bundle pointer serving channel이 Launcher 요청 channel과 일치하지 않습니다.");
            return context.BundleOriginChannel;
        }

        public static void ValidateForActivation(
            ModuleBundlePointerContext context,
            string servingChannel,
            string expectedCurrentBundleRevision,
            string expectedCurrentBundleLockSha256,
            string bundleRevision,
            string bundleLockSha256)
        {
            if (context == null) return;
            if (!BundleRevisionPattern.IsMatch(context.BundleRevision ?? "") ||
                !Sha256Pattern.IsMatch(context.BundleLockSha256 ?? "") || context.PointerGeneration < 1)
                throw new InvalidOperationException("Server Bundle pointer identity/generation 형식이 올바르지 않습니다.");
            if (!String.Equals(context.ServingChannel, servingChannel, StringComparison.Ordinal) ||
                !String.Equals(context.BundleRevision, bundleRevision, StringComparison.Ordinal) ||
                !String.Equals(context.BundleLockSha256, bundleLockSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Server Bundle pointer가 다운로드한 Bundle Lock identity와 일치하지 않습니다.");

            var hasPromotion = context.Promotion != null;
            var hasRollback = context.Rollback != null;
            if (hasPromotion && hasRollback)
                throw new InvalidOperationException("Stable pointer에는 promotion과 rollback authorization을 동시에 사용할 수 없습니다.");

            if (String.Equals(servingChannel, "staging", StringComparison.Ordinal))
            {
                if (!String.Equals(context.BundleOriginChannel, "staging", StringComparison.Ordinal) || hasPromotion || hasRollback)
                    throw new InvalidOperationException("Staging pointer는 staging-origin Bundle만 직접 제공할 수 있습니다.");
                return;
            }

            if (hasPromotion)
            {
                ValidatePromotion(context, expectedCurrentBundleRevision, expectedCurrentBundleLockSha256);
                return;
            }
            if (hasRollback)
            {
                ValidateRollback(context, expectedCurrentBundleRevision, expectedCurrentBundleLockSha256);
                return;
            }
            if (!String.Equals(context.BundleOriginChannel, "stable", StringComparison.Ordinal))
                throw new InvalidOperationException(StablePromotionRequiredCode + ": staging-origin Bundle을 Stable로 제공하려면 Server promotion authorization이 필요합니다.");
        }

        private static void ValidatePromotion(
            ModuleBundlePointerContext context,
            string expectedCurrentBundleRevision,
            string expectedCurrentBundleLockSha256)
        {
            var value = context.Promotion;
            DateTimeOffset promotedAt;
            Guid promotionId;
            Guid verificationId;
            if (!String.Equals(context.BundleOriginChannel, "staging", StringComparison.Ordinal) ||
                value.SchemaVersion != 1 || !Guid.TryParse(value.PromotionId, out promotionId) ||
                !Guid.TryParse(value.StagingVerificationId, out verificationId) ||
                !String.Equals(value.SourceChannel, "staging", StringComparison.Ordinal) ||
                !String.Equals(value.TargetChannel, "stable", StringComparison.Ordinal) ||
                !String.Equals(value.BundleRevision, context.BundleRevision, StringComparison.Ordinal) ||
                !String.Equals(value.BundleLockSha256, context.BundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(value.PreviousStableBundleRevision, expectedCurrentBundleRevision, StringComparison.Ordinal) ||
                !Sha256Pattern.IsMatch(expectedCurrentBundleLockSha256 ?? "") ||
                !String.Equals(value.PreviousStableBundleLockSha256, expectedCurrentBundleLockSha256, StringComparison.Ordinal) ||
                value.StablePointerGeneration != context.PointerGeneration ||
                !TryUtc(value.PromotedAtUtc, out promotedAt))
                throw new InvalidOperationException(StablePromotionMismatchCode + ": Stable promotion authorization이 exact Staging Bundle/이전 Stable CAS와 일치하지 않습니다.");
        }

        private static void ValidateRollback(
            ModuleBundlePointerContext context,
            string expectedCurrentBundleRevision,
            string expectedCurrentBundleLockSha256)
        {
            var value = context.Rollback;
            DateTimeOffset rolledBackAt;
            Guid rollbackId;
            Guid promotionId;
            if (value.SchemaVersion != 1 || !Guid.TryParse(value.RollbackId, out rollbackId) ||
                !Guid.TryParse(value.SourcePromotionId, out promotionId) ||
                !String.Equals(value.TargetChannel, "stable", StringComparison.Ordinal) ||
                !String.Equals(value.BundleRevision, context.BundleRevision, StringComparison.Ordinal) ||
                !String.Equals(value.BundleLockSha256, context.BundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(value.ReplacedBundleRevision, expectedCurrentBundleRevision, StringComparison.Ordinal) ||
                !Sha256Pattern.IsMatch(expectedCurrentBundleLockSha256 ?? "") ||
                !String.Equals(value.ReplacedBundleLockSha256, expectedCurrentBundleLockSha256, StringComparison.Ordinal) ||
                value.StablePointerGeneration != context.PointerGeneration ||
                !TryUtc(value.RolledBackAtUtc, out rolledBackAt))
                throw new InvalidOperationException(StableRollbackMismatchCode + ": Stable rollback authorization이 복원 Bundle/현재 Stable CAS와 일치하지 않습니다.");
        }

        private static bool TryUtc(string text, out DateTimeOffset value)
        {
            value = default(DateTimeOffset);
            return !String.IsNullOrWhiteSpace(text) && text.EndsWith("Z", StringComparison.Ordinal) &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value) &&
                value.Offset == TimeSpan.Zero;
        }

        private static void ValidateChannel(string value, string name)
        {
            if (value != "stable" && value != "staging")
                throw new InvalidOperationException(name + "이 올바르지 않습니다.");
        }
    }
}
