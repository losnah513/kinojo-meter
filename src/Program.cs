using System;
using System.Threading;
using System.Windows;

namespace KinojoMeterPrototype
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool ownsInstance;
            using (var singleInstance = new Mutex(true, @"Local\KINOJO_Meter_SingleInstance", out ownsInstance))
            {
                if (!ownsInstance) return;
                try
                {
                    DiagnosticLog.Info("APP", "KINOJO Meter " + KinojoVersion.Current + " started");
                    KinojoVersion.ValidateInstalledManifest();
                    var app = new Application();
                    app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    app.Run(new MainWindow());
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Error("APP", "Fatal startup failure", ex);
                    try
                    {
                        MessageBox.Show("프로그램 시작 중 오류가 발생했습니다.\n\n관리자 진단 로그를 확인해 주세요.", "KINOJO Meter", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    catch { }
                }
                finally
                {
                    try { singleInstance.ReleaseMutex(); }
                    catch (ApplicationException) { }
                }
            }
        }
    }
}
