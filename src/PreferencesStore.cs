using System;
using System.IO;
using System.Web.Script.Serialization;

namespace KinojoMeterPrototype
{
    internal static class PreferencesStore
    {
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KINOJO Meter");
        private static readonly string FilePath = Path.Combine(DirectoryPath, "ui-preferences.json");
        private static readonly string LegacyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KINOJO Meter Test",
            "ui-preferences.json");

        public static MeterPreferences Load()
        {
            try
            {
                var source = File.Exists(FilePath) ? FilePath : LegacyFilePath;
                if (!File.Exists(source)) return MeterPreferences.Default();
                var serializer = new JavaScriptSerializer();
                var value = serializer.Deserialize<MeterPreferences>(File.ReadAllText(source));
                if (value != null && !String.Equals(source, FilePath, StringComparison.OrdinalIgnoreCase)) Save(value);
                return value ?? MeterPreferences.Default();
            }
            catch
            {
                return MeterPreferences.Default();
            }
        }

        public static void Save(MeterPreferences preferences)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var serializer = new JavaScriptSerializer();
                File.WriteAllText(FilePath, serializer.Serialize(preferences));
            }
            catch
            {
                // UI settings never block Meter startup.
            }
        }
    }
}
