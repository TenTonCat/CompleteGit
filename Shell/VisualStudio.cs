using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace CompleteGit.Shell;

/// <summary>
/// Opens a file inside its Visual Studio solution (e.g. a Unity project's generated .sln) so
/// navigation works. Reuses a running instance that already has the solution open, otherwise
/// launches Visual Studio and waits for it to load the solution. Talks to VS through the DTE
/// automation objects it registers in the COM running object table.
/// </summary>
public static class VisualStudio
{
    /// <summary>Opens <paramref name="file"/> in its solution. Returns a status line for the console.</summary>
    public static async Task<string> OpenInSolutionAsync(string file, string repoRoot)
    {
        file = Path.GetFullPath(file);
        if (!File.Exists(file)) throw new FileNotFoundException("File not in the working tree: " + file);

        // A solution that's already open wins, even if it isn't the nearest one on disk.
        foreach (var dte in RunningInstances())
        {
            var sln = SolutionOf(dte);
            if (sln != null && IsUnder(file, Path.GetDirectoryName(sln)!))
            {
                await OpenAsync(dte, file);
                return $"Opened {Path.GetFileName(file)} in {Path.GetFileName(sln)} (running Visual Studio)";
            }
        }

        var solution = FindSolution(file, repoRoot) ?? throw new InvalidOperationException(NoSolutionMessage(file, repoRoot));
        var devenv = FindDevenv();
        Process.Start(devenv != null
            ? new ProcessStartInfo(devenv, $"\"{solution}\"")
            : new ProcessStartInfo(solution) { UseShellExecute = true });

        // Wait for the new instance to register and load the solution, then open the file.
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);
            var dte = RunningInstances().FirstOrDefault(d => string.Equals(SolutionOf(d), solution, StringComparison.OrdinalIgnoreCase));
            if (dte == null) continue;
            await OpenAsync(dte, file);
            return $"Opened {Path.GetFileName(file)} in {Path.GetFileName(solution)}";
        }
        throw new TimeoutException($"Started Visual Studio with {Path.GetFileName(solution)} but it didn't finish loading within 2 minutes; open the file from there.");
    }

    /// <summary>Nearest .sln/.slnx walking up from the file to the repo root. Prefers the one named after its folder (Unity's convention).</summary>
    public static string? FindSolution(string file, string repoRoot)
    {
        var root = Path.GetFullPath(repoRoot).TrimEnd('\\');
        for (var dir = Path.GetDirectoryName(file); dir != null; dir = Path.GetDirectoryName(dir))
        {
            var slns = Directory.GetFiles(dir, "*.sln*").Where(s => Path.GetExtension(s).ToLowerInvariant() is ".sln" or ".slnx").Order().ToList();
            if (slns.Count > 0)
                return slns.FirstOrDefault(s => Path.GetFileNameWithoutExtension(s).Equals(Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase)) ?? slns[0];
            if (dir.TrimEnd('\\').Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return null;
    }

    private static string NoSolutionMessage(string file, string repoRoot)
    {
        for (var dir = Path.GetDirectoryName(file); dir != null && IsUnder(dir, repoRoot); dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "Assets")) && Directory.Exists(Path.Combine(dir, "ProjectSettings")))
                return $"No solution in the Unity project at {dir}. Unity generates it: in the editor use Assets → Open C# Project, or Preferences → External Tools → Regenerate project files.";
        return "No .sln found between this file and the repository root.";
    }

    private static async Task OpenAsync(dynamic dte, string file)
    {
        // VS rejects automation calls while it's busy (RPC_E_CALL_REJECTED etc.), notably while a solution loads; retry.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                dte.ItemOperations.OpenFile(file);
                AllowSetForegroundWindow(-1);
                dte.MainWindow.Activate();
                return;
            }
            catch (COMException) when (attempt < 30) { await Task.Delay(500); }
        }
    }

    private static string? FindDevenv()
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(vswhere, "-latest -property productPath")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var path = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private static string? SolutionOf(dynamic dte)
    {
        try { string name = dte.Solution.FullName; return string.IsNullOrEmpty(name) ? null : Path.GetFullPath(name); }
        catch { return null; }
    }

    private static bool IsUnder(string path, string dir) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);

    /// <summary>Every DTE object (one per running Visual Studio) in the running object table.</summary>
    private static List<object> RunningInstances()
    {
        var found = new List<object>();
        if (GetRunningObjectTable(0, out var rot) != 0 || CreateBindCtx(0, out var ctx) != 0) return found;
        rot.EnumRunning(out var monikers);
        var batch = new IMoniker[1];
        while (monikers.Next(1, batch, IntPtr.Zero) == 0)
        {
            try
            {
                batch[0].GetDisplayName(ctx, null, out var name);
                if (name.StartsWith("!VisualStudio.DTE.", StringComparison.Ordinal) && rot.GetObject(batch[0], out var dte) == 0)
                    found.Add(dte);
            }
            catch (COMException) { }
        }
        return found;
    }

    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx ctx);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
