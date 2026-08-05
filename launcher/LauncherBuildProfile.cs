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
        public const string CoreSigningKeyId = "kinojo-core-staging-rsa-2026-01";
        public const string CoreSigningPublicModulusBase64 = "jm468uMxskTyjjk4pkhJi6RcwAiwsKjPQABHmH5MHTx5nw/ClClexRu73rJA05ykG0KZeRJ4wF2WHwMk5k/E/uEXKzBSCnm+y4u8y8RPNnoqgaSC9vKGh9y1Gf3yRD1cfrEp6g+LSE0nKQlUeqpw1JQzk7tvxNo2KCgpenV7WzIPyEKzPb4wHfngPkfDiMC5SQsOmGsrA5eMOh4NWKZG+0XNhs3mAOCo8jO1ID6RlqmD4tnG7HsttcxQboRncLEGgUV7A88lj4nCXaNPjsBSPttfLto0E1E8NWksxMkqD+1Wz85Ckmuemw06p7Xn0djVtx1A2IWu+lnUAQXd++vo8Mux8iN4dmXfvJfRM6Y+7BM4a1splJIpwhmla/VE2PAcjfjnQulnTZTvWaK7SwSM5qyJewVW01d4s0ysGQ/ovFWRMZhl6zauH8GdL3Ul4oUHBXJPe9GHWBLaE9YtWYRMN5d3NFJaIGYpoxDscV0Rhq1uYRePI03T4Xy1zXnmZWfF";
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
