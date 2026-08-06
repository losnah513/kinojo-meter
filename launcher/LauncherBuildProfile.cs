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
        public const string CoreSigningKeyId = "kinojo-core-staging-rsa-2026-03";
        public const string CoreSigningPublicModulusBase64 = "yrEZye7O+ynLrZ2KzyMuTo/C/O09LtlYWde2NUpnEPGtk040I08a/Tlx2Y5gHaG/VGwvlYoxh61CRIB9jNi79JyPdiJCfsE//0W2g285f9dQLwufHz14FE7bY0a/kWcRG0fd+/gnr6A9T6pX2hLl3IPGbpiy7aCQlZdKOrEKwYliYEdt5T1vkJ55tbAY2n2bHA90raOed29uhBCNtp0/x3PqZ+4QiRtipWGlo5VHCNbUEifhH28t5s5h6L2XIGjDnkmD5MOv4tiXH+vXG/JssPlnzpcoStd7GeLFZz+X7+xVJk3yr5UIkTTcj7CoPQFBqNYtU+NLgqZjb9jGJYvD4tBJphd3NFIyZyD4f1kq0XQOXG8bLNdtnIgVCpZacKCodLca1Oi0qRGSfC3Tf/Ce3Xn8/pr5YIhLG4c/egslSGDSi+bfB577aEW3ry5S04SXoJXFnW2aq1RMwE0Xg7MBhx46NPc92l9H66WVJiKCWMKCRww+iNuNCzpSSB6CUMq5";
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
