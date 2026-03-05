using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

namespace LiuYun.Services
{
    public static class ClipboardRegistryService
    {
        private const string RegistryKeyPath = @"Software\Microsoft\Clipboard";
        private const string ValueName = "EnableClipboardHistory";
        private const string ShellHotKeyUsedValueName = "ShellHotKeyUsed";
        private const string AppRegistryKeyPath = @"Software\LiuYun";
        private const string PreferSystemWinVValueName = "PreferSystemWinV";
        private const string ExplorerAdvancedRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string DisabledHotkeysValueName = "DisabledHotkeys";

        private static bool SetWinVDisabledHotkeyFlag(bool disableWinV)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvancedRegistryKeyPath, true);
                if (key == null)
                {
                    return false;
                }

                string current = Convert.ToString(key.GetValue(DisabledHotkeysValueName)) ?? string.Empty;
                string normalized = current.ToUpperInvariant();

                if (disableWinV)
                {
                    if (!normalized.Contains('V'))
                    {
                        string updated = normalized + "V";
                        key.SetValue(DisabledHotkeysValueName, updated, RegistryValueKind.String);
                    }
                }
                else
                {
                    string updated = normalized.Replace("V", string.Empty);
                    if (string.IsNullOrEmpty(updated))
                    {
                        key.DeleteValue(DisabledHotkeysValueName, false);
                    }
                    else
                    {
                        key.SetValue(DisabledHotkeysValueName, updated, RegistryValueKind.String);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClipboardRegistryService: Failed to set DisabledHotkeys flag: {ex.Message}");
                return false;
            }
        }

        public static bool DisableWinVHotkey_Silent()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath, true))
                {
                    if (key == null)
                    {
                        Debug.WriteLine("ClipboardRegistryService: Failed to create/open registry key.");
                        return false;
                    }

                    key.SetValue(ValueName, 0, RegistryValueKind.DWord);
                    SetWinVDisabledHotkeyFlag(true);
                    SetPreferSystemWinV(false);
                    Debug.WriteLine("ClipboardRegistryService: Disabled Windows clipboard history (EnableClipboardHistory=0).");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClipboardRegistryService: Failed to disable Win+V (silent): {ex.Message}");
                return false;
            }
        }

        public static bool RestoreSystemClipboard()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath, true))
                {
                    if (key == null)
                    {
                        Debug.WriteLine("ClipboardRegistryService: Failed to create/open registry key.");
                        return false;
                    }

                    key.SetValue(ValueName, 1, RegistryValueKind.DWord);
                    key.DeleteValue(ShellHotKeyUsedValueName, false);
                    SetWinVDisabledHotkeyFlag(false);
                    SetPreferSystemWinV(true);
                    Debug.WriteLine("ClipboardRegistryService: Restored Windows clipboard history (EnableClipboardHistory=1).");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClipboardRegistryService: Failed to restore Win+V: {ex.Message}");
                return false;
            }
        }

        public static bool RestoreSystemClipboard_WithRestart()
        {
            if (!RestoreSystemClipboard())
            {
                return false;
            }

            Thread.Sleep(500);
            bool restarted = ExplorerRestartService.RestartExplorer();
            return restarted;
        }

        public static bool IsWinVDisabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    var value = key.GetValue(ValueName);
                    if (value == null)
                    {
                        return false;
                    }

                    int intValue = Convert.ToInt32(value);
                    return intValue == 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClipboardRegistryService: Failed to check Win+V status: {ex.Message}");
                return false;
            }
        }

        public static bool GetPreferSystemWinV()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(AppRegistryKeyPath, false);
                object? value = key?.GetValue(PreferSystemWinVValueName);
                if (value == null)
                {
                    return true;
                }

                bool prefer = Convert.ToInt32(value) == 1;
                return prefer;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClipboardRegistryService: Failed to read PreferSystemWinV: {ex.Message}");
                return true;
            }
        }

        public static void SetPreferSystemWinV(bool preferSystemWinV)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(AppRegistryKeyPath, true);
                if (key == null)
                {
                    return;
                }

                key.SetValue(PreferSystemWinVValueName, preferSystemWinV ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClipboardRegistryService: Failed to write PreferSystemWinV: {ex.Message}");
            }
        }

    }
}
