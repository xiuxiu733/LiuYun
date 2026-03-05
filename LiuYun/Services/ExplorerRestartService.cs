using System;
using System.Diagnostics;
using System.Threading;

namespace LiuYun.Services
{
    public static class ExplorerRestartService
    {
        private static int CountRunningProcesses(string processName)
        {
            var processes = Process.GetProcessesByName(processName);
            try
            {
                return processes.Length;
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        public static bool RestartExplorer()
        {
            try
            {
                Debug.WriteLine("ExplorerRestartService: stopping Explorer...");

                var explorerProcesses = Process.GetProcessesByName("explorer");
                foreach (var process in explorerProcesses)
                {
                    using (process)
                    {
                        try
                        {
                            process.Kill();
                            process.WaitForExit(3000);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"ExplorerRestartService: failed to stop process: {ex.Message}");
                        }
                    }
                }

                Debug.WriteLine("ExplorerRestartService: Explorer stopped. Starting explorer.exe...");
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = true
                });

                Thread.Sleep(800);
                int runningCount = CountRunningProcesses("explorer");
                Debug.WriteLine($"ExplorerRestartService: explorer running count={runningCount}");
                if (runningCount == 0)
                {
                    Debug.WriteLine("ExplorerRestartService: explorer.exe did not start.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExplorerRestartService: failed to restart Explorer: {ex.Message}");
                return false;
            }
        }
    }
}
