using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace WinPECloner
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowCrashAndFallback(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowCrashAndFallback(e.ExceptionObject as Exception);

            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                ShowCrashAndFallback(ex);
            }
        }

        private static void ShowCrashAndFallback(Exception? ex)
        {
            string msg = ex != null
                ? $"Si è verificato un errore all'avvio di TessyClone:\n\n{ex.GetType().FullName}: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}"
                : "Si è verificato un errore fatale non specificato in TessyClone.";

            try
            {
                MessageBox.Show(msg, "TessyClone — Errore Fatale all'Avvio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // Fallback se la GUI non risponde
            }

            // Fallback di emergenza: apre cmd.exe per impedire il reboot loop in WinPE
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    UseShellExecute = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
            }
            catch
            {
            }
        }
    }
}
