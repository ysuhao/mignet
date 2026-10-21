using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DesktopStamp.HostAdapters
{
    /// <summary>
    /// Per-phase stopwatch for one stamp/probe pipeline run. Produces a compact "phase=12ms ..." summary
    /// that is attached to <see cref="StampPlacementResult.Diagnostics"/> and written to the stamp log.
    /// </summary>
    public sealed class PhaseTimer
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly StringBuilder _phases = new StringBuilder();
        private double _lastMs;

        public void Mark(string phase)
        {
            double now = _watch.Elapsed.TotalMilliseconds;
            if (_phases.Length > 0)
                _phases.Append(' ');
            _phases.Append(phase).Append('=').Append((now - _lastMs).ToString("F0")).Append("ms");
            _lastMs = now;
        }

        public override string ToString() => $"{_phases} total={_watch.Elapsed.TotalMilliseconds:F0}ms";
    }

    /// <summary>
    /// Append-only daily text log under %LOCALAPPDATA%\DesktopStamp\logs so failed or slow stamps can be
    /// diagnosed after the fact. Logging never throws.
    /// </summary>
    public static class StampDiagnosticsLog
    {
        private static readonly object Sync = new object();

        public static string LogDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopStamp",
            "logs");

        public static void Append(string message)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                lock (Sync)
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(Path.Combine(LogDirectory, $"stamp-{DateTime.Now:yyyyMMdd}.log"), line, Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics must never break stamping
            }
        }

        public static string Describe(Exception ex) => ex == null ? string.Empty : $"{ex.GetType().Name}: {ex.Message}";
    }
}
