using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace KinojoMeterLauncher
{
    internal static class LauncherVersion
    {
        public const string Channel = LauncherBuildProfile.Channel;

        public static bool IsStaging
        {
            get { return String.Equals(Channel, "staging", StringComparison.Ordinal); }
        }

        public static string Current
        {
            get
            {
                var value = Assembly.GetExecutingAssembly().GetName().Version;
                return value == null ? "0.0.0" : value.Major + "." + value.Minor + "." + Math.Max(0, value.Build);
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            using (var singleInstance = new Mutex(true, LauncherBuildProfile.MutexName, out var created))
            {
                if (!created)
                {
                    MessageBox.Show("KINOJO Meter Launcher" + LauncherBuildProfile.DisplaySuffix + "가 이미 실행 중입니다.", "KINOJO Meter" + LauncherBuildProfile.DisplaySuffix, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
                {
                    Trace.WriteLine(args.ExceptionObject);
                };
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                LauncherLoginResult login;
                using (var loginForm = new LauncherLoginForm())
                {
                    if (loginForm.ShowDialog() != DialogResult.OK || loginForm.LoginResult == null) return;
                    login = loginForm.LoginResult;
                }

                var sessionHandedOff = false;
                try
                {
                    using (var launcherForm = new LauncherForm(login))
                    {
                        Application.Run(launcherForm);
                        sessionHandedOff = launcherForm.SessionHandedOff;
                    }
                }
                finally
                {
                    if (!sessionHandedOff && login != null && !String.IsNullOrWhiteSpace(login.SessionToken))
                    {
                        using (var api = new LauncherApiClient())
                        {
                            api.LogoutAsync(login.SessionToken).GetAwaiter().GetResult();
                        }
                    }
                }
            }
        }
    }
}
