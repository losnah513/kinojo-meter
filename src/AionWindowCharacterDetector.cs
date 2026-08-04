using System;
using System.Collections.Generic;
using System.Linq;

namespace KinojoMeterPrototype
{
    internal static class AionWindowCharacterDetector
    {
        public static string MatchOwnedCharacter(string windowTitle, IEnumerable<string> ownedCharacterNames)
        {
            if (String.IsNullOrWhiteSpace(windowTitle) || !LooksLikeAionWindowTitle(windowTitle)) return "";

            var normalizedTitle = Normalize(windowTitle);
            var matches = (ownedCharacterNames ?? Enumerable.Empty<string>())
                .Select(value => (value ?? "").Trim())
                .Where(value => value.Length > 0)
                .Where(value =>
                {
                    var normalizedName = Normalize(value);
                    return normalizedName.Length >= 2 &&
                           normalizedTitle.IndexOf(normalizedName, StringComparison.OrdinalIgnoreCase) >= 0;
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return matches.Count == 1 ? matches[0] : "";
        }

        public static bool LooksLikeAionWindowTitle(string windowTitle)
        {
            var title = (windowTitle ?? "").Trim();
            return title.IndexOf("AION2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   title.IndexOf("아이온2", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Normalize(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            return new string(value.Trim().Where(Char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }
    }
}
