using System;
using System.IO;

namespace LeXtudio.UI.Text.Core
{
    /// <summary>
    /// Controls IME diagnostics output for TextCore adapters.
    /// Logging is disabled by default and can be enabled at runtime.
    /// </summary>
    public static class ImeLogging
    {
        private static readonly object s_lock = new();
        private static bool s_enabled;

        public static bool Enabled
        {
            get => s_enabled;
            set
            {
                s_enabled = value;
                Environment.SetEnvironmentVariable("UNOEDIT_DEBUG_IME", value ? "1" : null);
            }
        }

        public static string LogPath { get; } = Path.Combine(Path.GetTempPath(), "unoedit_ime.log");

        public static void Enable() => Enabled = true;

        public static void Disable() => Enabled = false;

        public static void Reset()
        {
            lock (s_lock)
            {
                if (File.Exists(LogPath))
                {
                    File.Delete(LogPath);
                }
            }
        }

        internal static void AppendLine(string line)
        {
            if (!s_enabled)
            {
                return;
            }

            lock (s_lock)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
    }
}
