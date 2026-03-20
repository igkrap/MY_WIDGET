using System;
using System.Reflection;
using Microsoft.Win32;

namespace TodoWidget.Services
{
    public class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TodoWidget";

        public bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    var value = key != null ? key.GetValue(ValueName) as string : null;
                    return !string.IsNullOrWhiteSpace(value);
                }
            }
            catch
            {
                return false;
            }
        }

        public bool SetEnabled(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    if (enabled)
                    {
                        key.SetValue(ValueName, BuildCommand());
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }

                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string BuildCommand()
        {
            var location = Assembly.GetEntryAssembly() != null
                ? Assembly.GetEntryAssembly().Location
                : Assembly.GetExecutingAssembly().Location;

            return "\"" + location + "\"";
        }
    }
}
