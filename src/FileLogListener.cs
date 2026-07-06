using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;

namespace OldenPedia
{
    /// <summary>
    /// Tees the entire BepInEx/Unity console to a flushed file so logs aren't
    /// lost to the in-game console buffer. Writes BepInEx/OldenPedia/console.txt.
    /// (BepInEx also writes BepInEx/LogOutput.log by default — this is a
    /// convenience copy in the mod's own folder.)
    /// </summary>
    internal sealed class FileLogListener : ILogListener
    {
        private StreamWriter _w;

        public FileLogListener()
        {
            try
            {
                var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "OldenPedia");
                Directory.CreateDirectory(dir);
                _w = new StreamWriter(Path.Combine(dir, "console.txt"), false) { AutoFlush = true };
                _w.WriteLine($"# OldenPedia console capture — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch { _w = null; }
        }

        // Capture all levels.
        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs e)
        {
            if (_w == null) return;
            try
            {
                string src = e.Source != null ? e.Source.SourceName : "?";
                _w.WriteLine($"[{e.Level,-7}:{src}] {e.Data}");
            }
            catch { }
        }

        public void Dispose()
        {
            try { if (_w != null) _w.Dispose(); } catch { }
            _w = null;
        }
    }
}
