using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace CompleteGit;

/// <summary>
/// Runs the app from a copy under %LOCALAPPDATA%\CompleteGit\run so the folder it was launched from (publish\) is never
/// locked and can be republished while the app is open. The running copy watches that folder and reports a newer build.
/// </summary>
public static class Updater
{
    private const string SourceEnv = "COMPLETEGIT_SOURCE";
    private const string MainDll = "CompleteGit.dll";
    private static readonly string RunRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompleteGit", "run");

    /// <summary>The folder this copy was launched from, or null when running in place.</summary>
    public static string? SourceDir { get; private set; }

    /// <summary>
    /// Copies the app to a fresh run folder and starts it there. Returns true when the copy was started and this
    /// process should exit; false to carry on in place (already a copy, debugging, or the copy failed).
    /// </summary>
    public static bool RelaunchFromCopy(string[] args)
    {
        var here = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var source = Environment.GetEnvironmentVariable(SourceEnv);
        if (source != null)
        {
            // Don't hand it on to git or console commands.
            Environment.SetEnvironmentVariable(SourceEnv, null);
            SourceDir = source;
            CleanOldCopies(here);
            return false;
        }
        if (Debugger.IsAttached) return false;
        try
        {
            var dest = Path.Combine(RunRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            CopyDir(here, dest);
            var psi = new ProcessStartInfo(Path.Combine(dest, "CompleteGit.exe")) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment[SourceEnv] = here;
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Starts the published build again (which makes a new copy) with the given repository.</summary>
    public static void LaunchSource(string? repo)
    {
        if (SourceDir == null) return;
        var psi = new ProcessStartInfo(Path.Combine(SourceDir, "CompleteGit.exe")) { UseShellExecute = false };
        if (repo != null) psi.ArgumentList.Add(repo);
        Process.Start(psi);
    }

    /// <summary>
    /// Polls the source folder and calls <paramref name="onUpdate"/> once with the new build's time when it changes.
    /// The build must look the same on two checks in a row so a publish still in progress isn't picked up.
    /// </summary>
    public static void Watch(Dispatcher dispatcher, Action<DateTime> onUpdate)
    {
        if (SourceDir == null) return;
        var running = File.GetLastWriteTimeUtc(Path.Combine(AppContext.BaseDirectory, MainDll));
        DateTime? pending = null;
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (s, _) =>
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(Path.Combine(SourceDir, MainDll)); } catch { return; }
            if (stamp <= running) { pending = null; return; }
            if (pending != stamp) { pending = stamp; return; }
            ((DispatcherTimer)s!).Stop();
            onUpdate(stamp.ToLocalTime());
        }, dispatcher);
        timer.Start();
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
    }

    /// <summary>Deletes run folders no instance is using. A copy's exe is locked while it runs, so that is tried first.</summary>
    private static void CleanOldCopies(string current)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(RunRoot))
            {
                if (string.Equals(dir, current, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var exe = Path.Combine(dir, "CompleteGit.exe");
                    if (File.Exists(exe)) File.Delete(exe);
                    Directory.Delete(dir, true);
                }
                catch { /* still running, try next time */ }
            }
        }
        catch { /* non-fatal */ }
    }
}
