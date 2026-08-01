using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace KinojoMeterPrototype
{
    internal static class KinojoVersion
    {
        private static readonly Assembly ExecutingAssembly = Assembly.GetExecutingAssembly();
        private static readonly string AssemblyDisplayVersion = ReadAssemblyDisplayVersion();
        private static readonly string AssemblyFileVersion = ReadAssemblyFileVersion();
        private static readonly string InstalledChannel = ReadInstalledManifestValue("channel", "stable");

        public static string Current { get { return AssemblyDisplayVersion; } }
        public static string FileVersion { get { return AssemblyFileVersion; } }
        public static string Channel { get { return InstalledChannel; } }

        public static void ValidateInstalledManifest()
        {
            try
            {
                var manifestPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.json");
                if (!File.Exists(manifestPath))
                {
                    DiagnosticLog.Info("VERSION", "Installed version.json is missing. Assembly version=" + Current);
                    return;
                }

                var json = File.ReadAllText(manifestPath);
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                object rawVersion;
                var manifestVersion = data != null && data.TryGetValue("version", out rawVersion)
                    ? Convert.ToString(rawVersion)
                    : "";
                object rawFileVersion;
                var manifestFileVersion = data != null && data.TryGetValue("fileVersion", out rawFileVersion)
                    ? Convert.ToString(rawFileVersion)
                    : "";

                if (!String.Equals(manifestVersion, Current, StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(manifestFileVersion, FileVersion, StringComparison.OrdinalIgnoreCase))
                {
                    DiagnosticLog.Info("VERSION", "Installed manifest mismatch. assembly=" + Current + "/" + FileVersion +
                        ", manifest=" + manifestVersion + "/" + manifestFileVersion);
                    return;
                }

                DiagnosticLog.Info("VERSION", "Installed version contract verified: " + Current);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("VERSION", "Installed version contract validation failed", ex);
            }
        }

        private static string ReadInstalledManifestValue(string key, string fallback)
        {
            try
            {
                var manifestPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.json");
                if (!File.Exists(manifestPath)) return fallback;
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifestPath));
                object value;
                var text = data != null && data.TryGetValue(key, out value) ? Convert.ToString(value) : "";
                return String.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
            }
            catch
            {
                return fallback;
            }
        }

        private static string ReadAssemblyDisplayVersion()
        {
            var version = ExecutingAssembly.GetName().Version;
            if (version == null) return "0.0.0";
            return version.Major + "." + version.Minor + "." + Math.Max(0, version.Build);
        }

        private static string ReadAssemblyFileVersion()
        {
            try
            {
                var value = FileVersionInfo.GetVersionInfo(ExecutingAssembly.Location).FileVersion;
                if (!String.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            catch { }
            return Current + ".0";
        }
    }
}
