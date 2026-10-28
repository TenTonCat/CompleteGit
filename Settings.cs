using System.IO;
using System.Text.Json;

namespace CompleteGit;

public sealed class Settings
{
    public string? LastRepo { get; set; }
    public List<string> RecentRepos { get; set; } = new();
    public string Shell { get; set; } = "pwsh";
    public string GitPath { get; set; } = "git";
    /// <summary>"head" = current branch only, "local" = local branches + their upstreams + tags, "all" = every remote branch too.</summary>
    public string LogScope { get; set; } = "local";
    public bool ShowQueries { get; set; } = true;
    public bool SideBySide { get; set; } = true;
    public bool WholeFile { get; set; }
    public string PullMode { get; set; } = "merge";
    /// <summary>Extra argument for the Push button: "", "--force-with-lease" or "--tags".</summary>
    public string PushMode { get; set; } = "";
    /// <summary>Order of the changed-file lists: "path", "name" or "ext".</summary>
    public string FileSort { get; set; } = "path";
    public bool FileSortDescending { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public double? CommitBoxHeight { get; set; }
    /// <summary>Background fetch interval, so branches show commits to pull; 0 turns it off.</summary>
    public int AutoFetchMinutes { get; set; } = 5;
    /// <summary>Unfinished commit message carried across a restart for an update, and the repository it belongs to.</summary>
    public string? CommitDraft { get; set; }
    public string? CommitDraftRepo { get; set; }

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CompleteGit");
    private static string FilePath => Path.Combine(Dir, "settings.json");
    /// <summary>Panel arrangement (AvalonDock XML), saved beside settings.json.</summary>
    public static string LayoutPath => Path.Combine(Dir, "layout.xml");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupt settings: start fresh */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }

    public void AddRecent(string repo)
    {
        RecentRepos.RemoveAll(r => string.Equals(r, repo, StringComparison.OrdinalIgnoreCase));
        RecentRepos.Insert(0, repo);
        if (RecentRepos.Count > 15) RecentRepos.RemoveRange(15, RecentRepos.Count - 15);
    }
}

/// <summary>Asks DWM for a dark title bar so dialogs match the theme.</summary>
public static class DarkTitleBar
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(System.Windows.Window w)
    {
        w.SourceInitialized += (_, _) =>
        {
            try
            {
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                int on = 1;
                DwmSetWindowAttribute(h, 20, ref on, sizeof(int));
            }
            catch { /* older Windows */ }
        };
    }
}
