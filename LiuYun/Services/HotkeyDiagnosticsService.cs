using System;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace LiuYun.Services
{
    public static class HotkeyDiagnosticsService
    {
        public static DiagnosticResult RunDiagnostics()
        {
            var result = new DiagnosticResult
            {
                IsRunningAsAdmin = IsAdministrator(),
                CanAccessRegistry = CanAccessClipboardRegistry(),
                ClipboardHistoryEnabled = IsClipboardHistoryEnabled()
            };

            result.WinVOccupiedBySystem = result.ClipboardHistoryEnabled;
            result.DiagnosticMessage = GenerateDiagnosticMessage(result);
            return result;
        }

        private static bool IsAdministrator()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HotkeyDiagnosticsService: IsAdministrator failed: {ex.Message}");
                return false;
            }
        }

        private static bool CanAccessClipboardRegistry()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Clipboard", true);
                if (key != null)
                {
                    return true;
                }

                using RegistryKey? newKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Clipboard", true);
                return newKey != null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HotkeyDiagnosticsService: CanAccessClipboardRegistry failed: {ex.Message}");
                return false;
            }
        }

        private static bool IsClipboardHistoryEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Clipboard", false);
                if (key == null)
                {
                    return true;
                }

                object? value = key.GetValue("EnableClipboardHistory");
                if (value == null)
                {
                    return true;
                }

                int intValue = Convert.ToInt32(value);
                return intValue != 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HotkeyDiagnosticsService: IsClipboardHistoryEnabled failed: {ex.Message}");
                return true;
            }
        }

        private static string GenerateDiagnosticMessage(DiagnosticResult result)
        {
            string adminText = result.IsRunningAsAdmin ? "是" : "否";
            string registryText = result.CanAccessRegistry ? "正常" : "受限";
            string clipboardText = result.ClipboardHistoryEnabled ? "启用" : "禁用";

            return
                "=== LiuYun 热键诊断报告 ===\n\n" +
                $"1. 管理员权限: {adminText}\n" +
                $"2. 注册表访问权限: {registryText}\n" +
                $"3. 系统剪贴板历史 (Win+V): {clipboardText}\n\n" +
                (result.ClipboardHistoryEnabled
                    ? "当前系统 Win+V 仍被系统剪贴板占用。建议在设置中禁用系统 Win+V，然后注销或重启后重试。\n\n"
                    : "系统 Win+V 已禁用，快捷键冲突风险较低。\n\n") +
                (!result.CanAccessRegistry
                    ? "检测到注册表访问受限，请检查管理员权限或安全软件拦截。\n\n"
                    : string.Empty) +
                (!result.IsRunningAsAdmin
                    ? "建议以管理员身份运行一次应用，排除权限因素。\n"
                    : "权限检查通过。\n");
        }

        public class DiagnosticResult
        {
            public bool IsRunningAsAdmin { get; set; }
            public bool CanAccessRegistry { get; set; }
            public bool ClipboardHistoryEnabled { get; set; }
            public bool WinVOccupiedBySystem { get; set; }
            public string DiagnosticMessage { get; set; } = string.Empty;
        }
    }
}
