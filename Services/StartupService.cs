using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace LiuYun.Services
{
    public sealed record StartupStateInfo(bool IsEnabled, bool CanToggle, string Description);

    public sealed class StartupService
    {
        private const string StartupTaskId = "LiuYunStartup";
        private const string ScheduledTaskName = "LiuYun_AutoStart";
        private const string StartupLaunchArgument = "--startup";
        private const string RegistryKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RegistryValueName = "LiuYun";
        private const int CommandTimeoutMs = 10000;

        private static readonly Lazy<bool> IsPackagedLazy = new(() =>
        {
            try
            {
                _ = Package.Current;
                return true;
            }
            catch
            {
                return false;
            }
        });

        private static bool IsPackaged => IsPackagedLazy.Value;
        private static readonly string ExecutablePath = Environment.ProcessPath ??
                                                       Process.GetCurrentProcess().MainModule?.FileName ??
                                                       string.Empty;

        public Task<StartupStateInfo> GetStateAsync()
        {
            return IsPackaged ? GetPackagedStateAsync() : Task.FromResult(GetUnpackagedState());
        }

        public Task<StartupStateInfo> EnableAsync()
        {
            return IsPackaged ? EnablePackagedAsync() : Task.FromResult(EnableUnpackaged());
        }

        public Task<StartupStateInfo> DisableAsync()
        {
            return IsPackaged ? DisablePackagedAsync() : Task.FromResult(DisableUnpackaged());
        }

        private static async Task<StartupStateInfo> GetPackagedStateAsync()
        {
            try
            {
                StartupTask startupTask = await StartupTask.GetAsync(StartupTaskId);
                return FromStartupTask(startupTask, null);
            }
            catch (Exception ex)
            {
                return new StartupStateInfo(false, false, $"Unable to query startup task: {ex.Message}");
            }
        }

        private static async Task<StartupStateInfo> EnablePackagedAsync()
        {
            try
            {
                StartupTask startupTask = await StartupTask.GetAsync(StartupTaskId);

                if (startupTask.State == StartupTaskState.DisabledByPolicy)
                {
                    return FromStartupTask(startupTask, "Disabled by policy - contact your administrator.");
                }

                if (startupTask.State == StartupTaskState.DisabledByUser)
                {
                    StartupTaskState result = await startupTask.RequestEnableAsync();
                    return FromStartupTask(startupTask, DescribeStartupResult(result));
                }

                if (startupTask.State != StartupTaskState.Enabled)
                {
                    StartupTaskState result = await startupTask.RequestEnableAsync();
                    return FromStartupTask(startupTask, DescribeStartupResult(result));
                }

                return FromStartupTask(startupTask, "Autostart already enabled.");
            }
            catch (Exception ex)
            {
                return new StartupStateInfo(false, false, $"Failed to enable autostart: {ex.Message}");
            }
        }

        private static async Task<StartupStateInfo> DisablePackagedAsync()
        {
            try
            {
                StartupTask startupTask = await StartupTask.GetAsync(StartupTaskId);

                if (startupTask.State == StartupTaskState.Enabled)
                {
                    startupTask.Disable();
                    return FromStartupTask(startupTask, "Autostart disabled.");
                }

                return FromStartupTask(startupTask, "Autostart already disabled.");
            }
            catch (Exception ex)
            {
                return new StartupStateInfo(false, false, $"Failed to disable autostart: {ex.Message}");
            }
        }

        private static StartupStateInfo FromStartupTask(StartupTask startupTask, string? overrideMessage)
        {
            bool canToggle = startupTask.State != StartupTaskState.DisabledByPolicy;
            bool isEnabled = startupTask.State == StartupTaskState.Enabled;

            string description = overrideMessage ?? startupTask.State switch
            {
                StartupTaskState.Disabled => "Autostart disabled.",
                StartupTaskState.DisabledByUser => "Disabled manually - toggle to re-enable.",
                StartupTaskState.DisabledByPolicy => "Disabled by policy - cannot toggle.",
                StartupTaskState.Enabled => "Autostart enabled.",
                _ => "Startup status unknown."
            };

            return new StartupStateInfo(isEnabled, canToggle, description);
        }

        private static string DescribeStartupResult(StartupTaskState state)
        {
            return state switch
            {
                StartupTaskState.Enabled => "Autostart enabled.",
                StartupTaskState.DisabledByUser => "User declined startup permission.",
                StartupTaskState.DisabledByPolicy => "Disabled by system policy.",
                _ => "Autostart disabled."
            };
        }

        private static StartupStateInfo GetUnpackagedState()
        {
            if (string.IsNullOrEmpty(ExecutablePath))
            {
                return new StartupStateInfo(false, false, "Unable to resolve executable path.");
            }

            try
            {
                if (TryQueryScheduledTaskXml(out string taskXml, out string queryError))
                {
                    bool configured = IsScheduledTaskConfiguredForCurrentExecutable(taskXml);
                    return configured
                        ? new StartupStateInfo(true, true, "Autostart enabled (Task Scheduler).")
                        : new StartupStateInfo(false, true, "Task exists but command is outdated - toggle to refresh.");
                }

                if (IsAccessDenied(queryError))
                {
                    return new StartupStateInfo(false, true, "Need administrator permission for Task Scheduler. Click toggle to grant.");
                }

                if (IsLegacyRegistryAutostartEnabled())
                {
                    return new StartupStateInfo(true, true, "Autostart enabled (legacy registry).");
                }

                return new StartupStateInfo(false, true, "Autostart disabled.");
            }
            catch (Exception ex)
            {
                return new StartupStateInfo(false, false, $"Unable to read autostart setting: {ex.Message}");
            }
        }

        private static StartupStateInfo EnableUnpackaged()
        {
            if (string.IsNullOrEmpty(ExecutablePath))
            {
                return new StartupStateInfo(false, false, "Unable to resolve executable path.");
            }

            try
            {
                string startupCommand = $"\\\"{ExecutablePath}\\\" {StartupLaunchArgument}";
                string createArgs = $"/Create /SC ONLOGON /TN \"{ScheduledTaskName}\" /TR \"{startupCommand}\" /RL LIMITED /F";
                CommandResult createResult = RunSchtasks(createArgs);
                if (createResult.ExitCode != 0 && IsAccessDenied(createResult))
                {
                    createResult = RunSchtasksElevated(createArgs);
                }

                if (createResult.ExitCode != 0)
                {
                    return new StartupStateInfo(false, false, $"Failed to enable autostart task: {BuildCommandError(createResult)}");
                }

                RemoveLegacyRegistryRunEntry();
                return GetUnpackagedState();
            }
            catch (Exception ex)
            {
                return new StartupStateInfo(false, false, $"Failed to enable autostart: {ex.Message}");
            }
        }

        private static StartupStateInfo DisableUnpackaged()
        {
            try
            {
                string deleteArgs = $"/Delete /TN \"{ScheduledTaskName}\" /F";
                CommandResult deleteResult = RunSchtasks(deleteArgs);
                if (deleteResult.ExitCode != 0 && IsAccessDenied(deleteResult))
                {
                    deleteResult = RunSchtasksElevated(deleteArgs);
                }

                if (deleteResult.ExitCode != 0 && !IsTaskMissing(deleteResult))
                {
                    return new StartupStateInfo(true, false, $"Failed to disable autostart task: {BuildCommandError(deleteResult)}");
                }

                RemoveLegacyRegistryRunEntry();
                return new StartupStateInfo(false, true, "Autostart disabled.");
            }
            catch (Exception ex)
            {
                return new StartupStateInfo(false, false, $"Failed to disable autostart: {ex.Message}");
            }
        }

        private static bool TryQueryScheduledTaskXml(out string xml, out string error)
        {
            CommandResult queryResult = RunSchtasks($"/Query /TN \"{ScheduledTaskName}\" /XML");
            if (queryResult.ExitCode == 0)
            {
                xml = queryResult.StdOut;
                error = string.Empty;
                return true;
            }

            xml = string.Empty;
            error = BuildCommandError(queryResult);
            return false;
        }

        private static bool IsLegacyRegistryAutostartEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
                if (key?.GetValue(RegistryValueName) is not string rawValue)
                {
                    return false;
                }

                string normalized = NormalizePath(rawValue);
                return string.Equals(normalized, ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void RemoveLegacyRegistryRunEntry()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
                key?.DeleteValue(RegistryValueName, false);
            }
            catch
            {
            }
        }

        private static bool IsScheduledTaskConfiguredForCurrentExecutable(string taskXml)
        {
            if (string.IsNullOrWhiteSpace(taskXml))
            {
                return false;
            }

            try
            {
                XDocument doc = XDocument.Parse(taskXml);
                string command = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Command")?.Value ?? string.Empty;
                string arguments = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Arguments")?.Value ?? string.Empty;

                if (string.IsNullOrWhiteSpace(command))
                {
                    return false;
                }

                string normalizedCommand = NormalizePath(command);
                string normalizedExe = NormalizePath(ExecutablePath);
                if (!string.Equals(normalizedCommand, normalizedExe, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return ContainsStartupArgument(arguments);
            }
            catch
            {
                return taskXml.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                       taskXml.Contains(StartupLaunchArgument, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool ContainsStartupArgument(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return false;
            }

            string[] tokens = arguments.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Any(token =>
                string.Equals(token.Trim().Trim('"'), StartupLaunchArgument, StringComparison.OrdinalIgnoreCase));
        }

        private static CommandResult RunSchtasks(string arguments)
        {
            try
            {
                using Process process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                process.Start();
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(CommandTimeoutMs))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    string timedOutStdOut = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
                    string timedOutStdErr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "Operation timed out.";
                    return new CommandResult(-1, timedOutStdOut, timedOutStdErr);
                }

                process.WaitForExit();
                string stdout = stdoutTask.GetAwaiter().GetResult();
                string stderr = stderrTask.GetAwaiter().GetResult();
                return new CommandResult(process.ExitCode, stdout, stderr);
            }
            catch (Exception ex)
            {
                return new CommandResult(-1, string.Empty, ex.Message);
            }
        }

        private static CommandResult RunSchtasksElevated(string arguments)
        {
            try
            {
                using Process process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = arguments,
                        UseShellExecute = true,
                        Verb = "runas"
                    }
                };

                process.Start();
                if (!process.WaitForExit(CommandTimeoutMs))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    return new CommandResult(-1, string.Empty, "Operation timed out.");
                }

                return new CommandResult(process.ExitCode, string.Empty, string.Empty);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new CommandResult(-1, string.Empty, "UAC authorization was canceled.");
            }
            catch (Exception ex)
            {
                return new CommandResult(-1, string.Empty, ex.Message);
            }
        }

        private static bool IsAccessDenied(CommandResult result)
        {
            return IsAccessDenied($"{result.StdOut}\n{result.StdErr}");
        }

        private static bool IsAccessDenied(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("0x80070005", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTaskMissing(CommandResult result)
        {
            string text = $"{result.StdOut}\n{result.StdErr}";
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return text.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("not exist", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("找不到", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildCommandError(CommandResult result)
        {
            string message = !string.IsNullOrWhiteSpace(result.StdErr)
                ? result.StdErr.Trim()
                : result.StdOut.Trim();

            if (string.IsNullOrWhiteSpace(message))
            {
                message = $"ExitCode={result.ExitCode}";
            }

            return message;
        }

        private static string NormalizePath(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }

            string trimmed = raw.Trim();
            if (trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            {
                return trimmed.Trim('"');
            }

            int firstQuote = trimmed.IndexOf('"');
            int secondQuote = firstQuote >= 0 ? trimmed.IndexOf('"', firstQuote + 1) : -1;
            if (firstQuote == 0 && secondQuote > firstQuote)
            {
                return trimmed.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
            }

            int firstSpace = trimmed.IndexOf(' ');
            if (firstSpace > 0 && trimmed.Contains('\\'))
            {
                return trimmed.Substring(0, firstSpace);
            }

            return trimmed;
        }

        private readonly record struct CommandResult(int ExitCode, string StdOut, string StdErr);
    }
}
