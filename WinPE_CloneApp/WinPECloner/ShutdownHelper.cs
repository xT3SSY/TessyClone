using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinPECloner
{
    public static class ShutdownHelper
    {
        public static bool EnableShutdownPrivilege()
        {
            try
            {
                IntPtr hProcess = NativeMethods.GetCurrentProcess();
                if (!NativeMethods.OpenProcessToken(
                    hProcess,
                    NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY,
                    out IntPtr hToken))
                {
                    return false;
                }

                if (!NativeMethods.LookupPrivilegeValue(null, NativeMethods.SE_SHUTDOWN_NAME, out NativeMethods.LUID luid))
                {
                    return false;
                }

                var tp = new NativeMethods.TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = new NativeMethods.LUID_AND_ATTRIBUTES
                    {
                        Luid = luid,
                        Attributes = NativeMethods.SE_PRIVILEGE_ENABLED
                    }
                };

                return NativeMethods.AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                return false;
            }
        }

        public static void ShutdownComputer(Action<string>? log = null)
        {
            log?.Invoke("Invio comando di spegnimento del sistema...");

            // 1. Metodo primario WinPE: wpeutil shutdown
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wpeutil.exe",
                    Arguments = "shutdown",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                log?.Invoke($"wpeutil shutdown non riuscito: {ex.Message}. Tentativo con ExitWindowsEx...");
            }

            // 2. Metodo secondario: ExitWindowsEx
            try
            {
                EnableShutdownPrivilege();
                NativeMethods.ExitWindowsEx(NativeMethods.EWX_SHUTDOWN | NativeMethods.EWX_FORCE, 0);
            }
            catch (Exception ex)
            {
                log?.Invoke($"ExitWindowsEx non riuscito: {ex.Message}");
            }
        }
    }
}
