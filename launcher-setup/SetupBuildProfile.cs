namespace KinojoMeterLauncherSetup
{
    internal static class SetupBuildProfile
    {
#if KINOJO_STAGING
        public const string ProductName = "KINOJO Meter Launcher STAGING";
        public const string InstallFolderName = "KINOJO Meter Staging";
        public const string ShortcutName = "KINOJO Meter STAGING";
        public const string UninstallKeyName = "KINOJO Meter Launcher STAGING";
        public const string ProcessName = "KINOJO.Meter.Launcher.Staging";
        public const string DataFolderName = "KINOJO Meter Staging";
        public const string CoreProcessName = "KINOJO.Meter.Staging";
        public const string LauncherFileName = "KINOJO.Meter.Launcher.Staging.exe";
        public const string SetupFileName = "KINOJO.Meter.Launcher.Staging.Setup.exe";
#else
        public const string ProductName = "KINOJO Meter Launcher";
        public const string InstallFolderName = "KINOJO Meter";
        public const string ShortcutName = "KINOJO Meter";
        public const string UninstallKeyName = "KINOJO Meter Launcher";
        public const string ProcessName = "KINOJO.Meter.Launcher";
        public const string DataFolderName = "KINOJO Meter";
        public const string CoreProcessName = "KINOJO.Meter";
        public const string LauncherFileName = "KINOJO.Meter.Launcher.exe";
        public const string SetupFileName = "KINOJO.Meter.Launcher.Setup.exe";
#endif
    }
}
