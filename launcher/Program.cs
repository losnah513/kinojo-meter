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
                Application.Run(new LauncherForm());
            }
        }
    }
}
