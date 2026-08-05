using System;
using System.Linq;
using System.Windows.Forms;

namespace KinojoMeterLauncherSetup
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                var uninstall = args != null && args.Any(value => String.Equals(value, "/uninstall", StringComparison.OrdinalIgnoreCase));
                var silent = args != null && args.Any(value => String.Equals(value, "/silent", StringComparison.OrdinalIgnoreCase));
                if (uninstall) LauncherSetupEngine.Uninstall(silent);
                else LauncherSetupEngine.Install(silent);
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, SetupBuildProfile.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
        }
    }
}
