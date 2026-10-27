using System.Windows.Media;

namespace CompleteGit.Git;

public sealed class FileStatus
{
    public string Path { get; init; } = "";
    public string? OldPath { get; init; }
    public char X { get; init; } = ' ';
    public char Y { get; init; } = ' ';

    public bool Untracked => X == '?';
    public bool Ignored => X == '!';
    public bool Conflicted => X == 'U' || Y == 'U' || (X == 'A' && Y == 'A') || (X == 'D' && Y == 'D');
    public bool HasStaged => !Untracked && !Ignored && !Conflicted && X != ' ';
    public bool HasUnstaged => Untracked || Conflicted || Y != ' ';
    public string Display => OldPath != null ? $"{OldPath} → {Path}" : Path;
    public override string ToString() => Display;
}

/// <summary>A row in the unstaged / staged / commit file lists.</summary>
public sealed class ChangeEntry
{
    public FileStatus File { get; }
    public bool Staged { get; }
    public string Code { get; }
    public string Path => File.Path;
    public string Display => File.Display;
    /// <summary>File name column; an untracked folder ("dir/") shows as "dir/".</summary>
    public string Name => Path.EndsWith('/') ? System.IO.Path.GetFileName(Path.TrimEnd('/')) + "/" : System.IO.Path.GetFileName(Path);
    /// <summary>Path column: the containing folder, plus where a rename came from.</summary>
    public string Folder
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(Path.TrimEnd('/'))?.Replace('\\', '/') ?? "";
            return File.OldPath != null ? $"{dir}  (from {File.OldPath})".TrimStart() : dir;
        }
    }
    /// <summary>Extension column, without the dot; Unity meta files show as "prefab.meta".</summary>
    public string Ext
    {
        get
        {
            var name = System.IO.Path.GetFileName(Path);
            bool meta = name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
            var ext = System.IO.Path.GetExtension(meta ? name[..^5] : name).TrimStart('.');
            return meta ? (ext.Length > 0 ? ext + ".meta" : "meta") : ext;
        }
    }
    public Brush CodeBrush { get; }
    public override string ToString() => Display;

    public ChangeEntry(FileStatus file, bool staged)
    {
        File = file;
        Staged = staged;
        Code = staged ? file.X.ToString()
             : file.Untracked ? "?"
             : file.Conflicted ? "U"
             : file.Y.ToString();
        CodeBrush = Code switch
        {
            "A" or "?" => new SolidColorBrush(Color.FromRgb(0x89, 0xD1, 0x85)),
            "D" => new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71)),
            "R" or "C" => new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)),
            "U" => new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6C)),
            _ => new SolidColorBrush(Color.FromRgb(0xE2, 0xC0, 0x8D)),
        };
    }
}

public enum RefKind { Head, LocalBranch, RemoteBranch, Tag }

public sealed class RefBadge
{
    public string Name { get; }
    public RefKind Kind { get; }
    public Brush Brush { get; }
    public RefBadge(string name, RefKind kind)
    {
        Name = name; Kind = kind;
        Brush = kind switch
        {
            RefKind.Head => new SolidColorBrush(Color.FromRgb(0x0E, 0x8A, 0x3E)),
            RefKind.LocalBranch => new SolidColorBrush(Color.FromRgb(0x0E, 0x63, 0x9C)),
            RefKind.RemoteBranch => new SolidColorBrush(Color.FromRgb(0x6B, 0x3F, 0xA0)),
            _ => new SolidColorBrush(Color.FromRgb(0x9A, 0x6B, 0x14)),
        };
    }
}

public sealed class CommitInfo
{
    public string Hash { get; init; } = "";
    public string ShortHash => Hash.Length >= 8 ? Hash[..8] : Hash;
    public string[] Parents { get; init; } = Array.Empty<string>();
    public string Author { get; init; } = "";
    public string Email { get; init; } = "";
    public DateTimeOffset Date { get; init; }
    public string DateText => IsWorkingTree ? "" : Date.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    public string Subject { get; set; } = "";
    public List<RefBadge> Refs { get; init; } = new();
    public GraphRow? Graph { get; set; }
    public bool IsWorkingTree { get; init; }
    public bool IsMerge => Parents.Length > 1;
    public string DisplayHash => IsWorkingTree ? "" : ShortHash;
    public override string ToString() => Subject;
}

/// <summary>A commit in a file's history (or in the history of some of its lines).</summary>
public sealed class HistoryEntry
{
    public CommitInfo Commit { get; init; } = new();
    /// <summary>The file's path in this commit (it can change across renames).</summary>
    public string Path { get; init; } = "";
    /// <summary>The path in the parent, when this commit renamed the file.</summary>
    public string? PrevPath { get; set; }
    /// <summary>For line history: git's patch of just the followed lines in this commit.</summary>
    public string? LinePatch { get; init; }
    public string PathText => PrevPath != null && PrevPath != Path ? $"{PrevPath} → {Path}" : Path;
}

/// <summary>One line of git blame output.</summary>
public sealed class BlameLine
{
    public int LineNo { get; init; }
    /// <summary>Line number in the commit that last changed it (for following that line's history further back).</summary>
    public int OrigLineNo { get; init; }
    public string Hash { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTimeOffset Date { get; init; }
    public string Summary { get; init; } = "";
    /// <summary>The file's path in <see cref="Hash"/>, and its parent commit/path ("previous" in git's porcelain output).</summary>
    public string FileName { get; init; } = "";
    public string? PrevHash { get; init; }
    public string? PrevFileName { get; init; }
    public string Text { get; init; } = "";
    /// <summary>First line of a run from the same commit; later lines in the run leave the commit columns blank.</summary>
    public bool FirstInRun { get; set; }

    public bool Uncommitted => Hash.All(c => c == '0');
    public string HashText => !FirstInRun ? "" : Uncommitted ? "—" : Hash[..8];
    public string AuthorText => FirstInRun ? (Uncommitted ? "Not committed yet" : Author) : "";
    public string DateText => FirstInRun && !Uncommitted ? Date.LocalDateTime.ToString("yyyy-MM-dd") : "";
    public string Tip => Uncommitted ? "Not committed yet" : $"{Hash[..8]}  {Author}  {Date.LocalDateTime:yyyy-MM-dd HH:mm}\n{Summary}";
}

public sealed class BranchInfo
{
    public string Name { get; init; } = "";
    public string FullRef { get; init; } = "";
    public bool IsRemote { get; init; }
    public bool IsCurrent { get; init; }
    public string? Upstream { get; init; }
    public string Track { get; init; } = "";
    public string Sha { get; init; } = "";
    public string Remote => IsRemote && Name.Contains('/') ? Name[..Name.IndexOf('/')] : "";
    public string ShortName => IsRemote && Name.Contains('/') ? Name[(Name.IndexOf('/') + 1)..] : Name;
}

public sealed class TagInfo
{
    public string Name { get; init; } = "";
    public string Sha { get; init; } = "";
}

public sealed class StashInfo
{
    public string Id { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed class RefSnapshot
{
    public List<BranchInfo> Branches { get; } = new();
    public List<TagInfo> Tags { get; } = new();
    public HashSet<string> RemoteBranchNames { get; } = new(StringComparer.Ordinal);
    public BranchInfo? Current => Branches.FirstOrDefault(b => b.IsCurrent);
}

public sealed class HeadInfo
{
    public string Branch { get; init; } = "";
    public bool Detached { get; init; }
    public string Sha { get; init; } = "";
    public string Display => Detached ? $"HEAD detached at {Sha}" : Branch;
}

public enum RepoOperation { None, Merge, Rebase, CherryPick, Revert }

// ---------- graph ----------

public enum EdgeKind { Through, In, Out }
public readonly record struct GraphEdge(int From, int To, int Color, EdgeKind Kind);

public sealed class GraphRow
{
    public int NodeLane { get; init; }
    public int NodeColor { get; init; }
    public bool IsMerge { get; init; }
    public List<GraphEdge> Edges { get; } = new();
}
