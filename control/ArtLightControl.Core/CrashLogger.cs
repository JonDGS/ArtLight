using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ArtLightControl
{
    /// <summary>
    /// Last-resort crash recorder — writes to %LocalAppData%\ArtLightControl\crash.log.
    ///
    /// Why this exists. Control had no unhandled-exception handler of any kind. A UI-thread
    /// exception was therefore stowed by the XAML framework, the process died with
    /// 0xc000027b (STATUS_STOWED_EXCEPTION), and *nothing* was written anywhere: not to
    /// debug.log, not to the Event Log beyond a faulting-module line. The Sessions page
    /// killed the app instantly and reproducibly and left no usable evidence — the crash
    /// signature in WER named combase.dll / E_INVALIDARG but carried no stack.
    ///
    /// Three hooks, deliberately belt-and-braces, because the stowed path is exactly the one
    /// that bypasses the ordinary handler:
    ///
    ///   FirstChanceException    Fires at the THROW, before any catch, including inside the
    ///                           XAML framework itself. This is the only hook guaranteed to
    ///                           see the exception that later gets stowed. Repeats of the same
    ///                           shape are throttled (see <see cref="RepeatWindow"/>) so a hot
    ///                           handled-exception loop cannot fill the file; a genuinely new
    ///                           throw always lands.
    ///   UnhandledException      AppDomain-level, for a fatal exit off the UI thread.
    ///   UnobservedTaskException A faulted background task nobody awaited.
    ///
    /// The XAML <c>Application.UnhandledException</c> hook is wired in App's constructor —
    /// it cannot live here, because this assembly deliberately carries no WinUI reference.
    ///
    /// Nothing here sets <c>Handled</c>. The crash behaviour is left exactly as it is today;
    /// the only change is that a record survives it.
    ///
    /// Writes use File.AppendAllText (open, write, close, flush) rather than a held
    /// StreamWriter, for the same reason <see cref="DebugLogger"/> does: a crash has to reach
    /// the disk even if the process dies mid-sentence, and the file must stay readable while
    /// the app runs. Unlike debug.log this file is never rolled — it is small and every line
    /// of it matters — but it is capped so a runaway throw loop cannot fill the disk.
    /// </summary>
    public static class CrashLogger
    {
        private const long MaxBytes = 4 * 1024 * 1024;

        /// <summary>A shape seen again inside this window is not written a second time.</summary>
        private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(2);

        private static readonly string CrashLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArtLightControl", "crash.log");

        // Explicit: File.AppendAllText's default encoding would prepend a BOM to a new file.
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        private static readonly object _lock = new();

        // Shape (type + HResult) → when it was last written, for the repeat throttle.
        private static readonly Dictionary<string, DateTime> _lastWrittenUtc = new();

        private static bool _installed;
        private static bool _directoryChecked;
        private static bool _sizeChecked;
        private static long _bytesOnDisk;

        /// <summary>Where the record lands, so callers can point a human at it.</summary>
        public static string CrashLogFilePath => CrashLogPath;

        /// <summary>
        /// Attaches the process-wide hooks. Call this as the very first statement of Main —
        /// before config reads, before logging is configured, before anything that can throw.
        /// Idempotent.
        /// </summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            AppDomain.CurrentDomain.FirstChanceException += (_, e) => FirstChance(e.Exception);

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log("FATAL / AppDomain.UnhandledException",
                    e.ExceptionObject as Exception,
                    "IsTerminating=" + e.IsTerminating);

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log("TaskScheduler.UnobservedTaskException", e.Exception, null);
                e.SetObserved();
            };
        }

        /// <summary>A plain marker line — used to trace how far a suspect path got.</summary>
        public static void Note(string message) => Write(message);

        /// <summary>
        /// Records an exception under a tag. <paramref name="note"/> carries anything the
        /// exception object cannot (a framework message, a terminating flag).
        /// </summary>
        public static void Log(string tag, Exception? ex, string? note)
        {
            var sb = new StringBuilder();
            sb.Append("===== ").Append(tag).Append(" =====");
            if (!string.IsNullOrEmpty(note)) sb.Append("  (").Append(note).Append(')');
            sb.AppendLine();

            if (ex is null)
            {
                sb.Append("  (no exception object supplied)");
            }
            else
            {
                AppendException(sb, ex, 0);
            }

            Write(sb.ToString());
        }

        private static void AppendException(StringBuilder sb, Exception ex, int depth)
        {
            string pad = new string(' ', depth * 2);

            sb.Append(pad)
              .Append(ex.GetType().FullName)
              .Append(" (0x").Append(ex.HResult.ToString("X8")).Append(')')
              .Append(": ").AppendLine(ex.Message);

            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                sb.Append(pad).AppendLine("  Stack:");
                foreach (string line in ex.StackTrace!.Split('\n'))
                    sb.Append(pad).Append("    ").AppendLine(line.TrimEnd());
            }

            if (ex.InnerException is not null)
            {
                sb.Append(pad).AppendLine("  --- inner ---");
                AppendException(sb, ex.InnerException, depth + 1);
            }
        }

        private static void FirstChance(Exception ex)
        {
            try
            {
                // Cheap key on purpose: building ex.StackTrace on every throw would make this
                // hook expensive, and HResult is the field that actually identifies the fault
                // (0x80070057 is what WER reported for this crash).
                string key = ex.GetType().FullName + "|" + ex.HResult.ToString("X8");

                lock (_lock)
                {
                    if (_lastWrittenUtc.TryGetValue(key, out DateTime last)
                        && DateTime.UtcNow - last < RepeatWindow)
                        return;
                    _lastWrittenUtc[key] = DateTime.UtcNow;
                }

                Log("first-chance", ex, null);
            }
            catch { }
        }

        private static void Write(string text)
        {
            try
            {
                lock (_lock)
                {
                    if (!_sizeChecked)
                    {
                        // Seed the cap from whatever is already there, so a restart cannot
                        // reset the budget and let the file grow without bound across runs.
                        try
                        {
                            var fi = new FileInfo(CrashLogPath);
                            _bytesOnDisk = fi.Exists ? fi.Length : 0;
                        }
                        catch { _bytesOnDisk = 0; }
                        _sizeChecked = true;
                    }

                    if (_bytesOnDisk >= MaxBytes) return;

                    if (!_directoryChecked)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
                        _directoryChecked = true;
                    }

                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}{Environment.NewLine}";
                    File.AppendAllText(CrashLogPath, line, Utf8NoBom);
                    _bytesOnDisk += line.Length;
                }
            }
            catch { }
        }
    }
}
