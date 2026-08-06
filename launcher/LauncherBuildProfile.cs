namespace KinojoMeterLauncher
{
    internal static class LauncherBuildProfile
    {
#if KINOJO_STAGING
        public const string Channel = "staging";
        public const string FunctionName = "meter-staging-ingest";
        public const string DataFolderName = "KINOJO Meter Staging";
        public const string MutexName = "Local\\KINOJO_Meter_Launcher_Staging";
        public const string DisplaySuffix = " STAGING";
        public const string CoreEntryPoint = "KINOJO.Meter.Staging.exe";
        public const string CoreProcessName = "KINOJO.Meter.Staging";
        public const string CoreSigningKeyId = "kinojo-core-staging-rsa-2026-02";
        public const string CoreSigningPublicModulusBase64 = "o6JysL9y80mftGWs6zXjur7Tu3iypUdbFQAOS8aUeVyusOAD05i3AWD0lBPRNYkaQNzAUXBWkEKecqdTsUW9X8xkTQePRNDRKp4MzxoyfmKQmAoV/M2lFGSQF9q5dFLP8dx7ShZGnK0lWb3yKgtxSqZj/K+HVuf0IcOumlBdiRaL7hOb182L9Ph9cpAxigzQ+VzXsXlW6Bdu16rIqu1RIulaifnhGzmuggHo31W1CmUGKy+ukDtRAMvmvks84F77fabpMjO/up+EbAEpAR32HSMACUBsuqnBq6UjdQMpG0tmqWd6hWS1AQ0KEZw/acPBcPg+HnBdUiP3pCIs4w5CgfHu9s4ga/7SVrVYD7K7SJcJ3y6PVzUwoDZVQLbeMHpEVGScrwfgA4fLhrbJ1IyAp72b+vbMVDZ7UTYPpUgXVm52rd+6wXxTyfCZ+ld0x0yOuJE5e5B8Z6ACFn3nUSsjuubCH6hW+vJ9xsl2JmnfnljEN5XK+BszoO8/l/GYq5zt";
#else
        public const string Channel = "stable";
        public const string FunctionName = "meter-ingest";
        public const string DataFolderName = "KINOJO Meter";
        public const string MutexName = "Local\\KINOJO_Meter_Launcher";
        public const string DisplaySuffix = "";
        public const string CoreEntryPoint = "KINOJO.Meter.exe";
        public const string CoreProcessName = "KINOJO.Meter";
        public const string CoreSigningKeyId = "kinojo-core-rsa-2026-01";
        public const string CoreSigningPublicModulusBase64 = "ybj1cE8V1GiCTUF83fSfBcf/lKYPNvtlYREmfnfjvP9aJ/791Gu4WKpqVPxwWAl/U99t9BHJJJXcSSMoCP/ay8uxlmNO3efIaS7nwZhmKuYAyUAZNFI181LK9laUnA20zbd7dmlH+YuiGhfW9x0d47ynJNzPR9vp80hBsIKqQEJ+xHEvQWJCapC/EAzRyMBoHeyy1Ff/ej713Z0+6GDNwVdBDh36M3SzHMbvVGGVh1xfQSkGXrQGitubrsJrUZDCZSNQgcJOBnxN3OuEoRxCX/LOzT/VzT28mL7suU+S///yMmwbwLMhUvoVGnVJ1vQ0L6jpUOo5YJ0OW9efMf4zc36LnhMFVT8w9kS3LDWFSPezAkhERlAbnp6FTZ8ZKTM/cgqTeB5FH316RL/xgescWFJYdNJSOZd1nXo0EzgqkGPy76PnDZlP2ObsQbtkVzD5Rxp+iJiBAeXEhG+VxoYw5NGiGPqrHVm/088T6NtKS/4aaDGhtH6Yz1hewTl/mGIV";
#endif
        public const string CoreSigningPublicExponentBase64 = "AQAB";
    }
}
