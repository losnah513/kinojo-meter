using System;
using System.Globalization;

namespace KinojoMeterLauncher
{
    internal static class LauncherPassKeyContract
    {
        public const int RequiredTextElements = 6;

        public static string Normalize(string value)
        {
            return (value ?? "").Trim().ToUpperInvariant();
        }

        public static bool IsValid(string value)
        {
            return TextElementCount(Normalize(value)) == RequiredTextElements;
        }

        public static int TextElementCount(string value)
        {
            return StringInfo.ParseCombiningCharacters(value ?? "").Length;
        }

        public static string[] TextElements(string value)
        {
            var text = value ?? "";
            var indexes = StringInfo.ParseCombiningCharacters(text);
            var result = new string[indexes.Length];
            for (var index = 0; index < indexes.Length; index++)
            {
                var start = indexes[index];
                var length = (index + 1 < indexes.Length ? indexes[index + 1] : text.Length) - start;
                result[index] = text.Substring(start, length);
            }
            return result;
        }
    }
}
