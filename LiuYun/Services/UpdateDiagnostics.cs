using System;
using System.Diagnostics;
using System.IO;

namespace LiuYun.Services
{
    public static class UpdateDiagnostics
    {
        private static readonly object SyncRoot = new object();

        public static string LogPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiuYun",
                "update-debug.log");

        public static void Log(string stage, string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{stage}] {message}";
            Debug.WriteLine(line);

            try
            {
                string? dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                lock (SyncRoot)
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
            }
        }
    }
}
