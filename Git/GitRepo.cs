using System.IO;
using CompleteGit.Shell;

namespace CompleteGit.Git;

/// <summary>Typed git operations. Every call goes through the <see cref="CommandRunner"/> so it shows up in the console.</summary>
public sealed class GitRepo
{
    public const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private readonly CommandRunner _r;

    public string Root { get; }
    public string GitDir { get; private set; }

    public GitRepo(CommandRunner runner, string root)
    {
        _r = runner;
        Root = root;
        GitDir = Path.Combine(root, ".git");
    }

    public static async Task<GitRepo?> OpenAsync(CommandRunner runner, string dir)
    {
        var res = await runner.GitAsync(dir, "rev-parse --show-toplevel --git-dir", quiet: true);
        if (!res.Ok) return null;
        var lines = res.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return null;
        var root = Path.GetFullPath(lines[0].Trim());
        var repo = new GitRepo(runner, root);
        var gd = lines[1].Trim();
        repo.GitDir = Path.IsPathRooted(gd) ? Path.GetFullPath(gd) : Path.GetFullPath(Path.Combine(root, gd));
        return repo;
    }

    // Query (quiet) vs. action (fully echoed) helpers.
    private Task<CommandResult> Q(string args) => _r.GitAsync(Root, args, quiet: true);
    /// <summary>Slow read-only query that runs beside the command queue and can be cancelled.</summary>
    private Task<CommandResult> BackgroundQ(string args, CancellationToken ct) => _r.GitAsync(Root, args, quiet: true, ct: ct, background: true);
    public Task<CommandResult> Run(string args, string? stdin = null) => _r.GitAsync(Root, args, quiet: false, stdin: stdin);

    public static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
    public static string QuoteAll(IEnumerable<string> paths) => string.Join(" ", paths.Select(Quote));

    // ---------------- queries ----------------

    public async Task<HeadInfo> HeadAsync()
    {
        var sym = await Q("symbolic-ref --short -q HEAD");
        var sha = await Q("rev-parse --short HEAD");
        var shaText = sha.Ok ? sha.Stdout.Trim() : "";
        if (sym.Ok && sym.Stdout.Trim().Length > 0)
            return new HeadInfo { Branch = sym.Stdout.Trim(), Sha = shaText };
        return new HeadInfo { Detached = true, Branch = shaText, Sha = shaText };
    }

    public async Task<List<FileStatus>> StatusAsync()
    {
        var res = await Q("status --porcelain=v1 -z --untracked-files=all");
        var list = new List<FileStatus>();
        if (!res.Ok) return list;
        var tokens = res.Stdout.Split('\0');
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.Length < 4) continue;
            char x = t[0], y = t[1];
            var path = t[3..];
            string? old = null;
            if (x == 'R' || x == 'C' || y == 'R' || y == 'C')
            {
                if (i + 1 < tokens.Length) { old = tokens[i + 1]; i++; }
            }
            list.Add(new FileStatus { Path = path, OldPath = old, X = x, Y = y });
        }
        return list;
    }

    public async Task<RefSnapshot> RefsAsync()
    {
        var snap = new RefSnapshot();
        var res = await Q("for-each-ref --sort=refname --format=%(refname)%09%(objectname:short)%09%(HEAD)%09%(upstream:short)%09%(upstream:track) refs/heads refs/remotes refs/tags");
        if (!res.Ok) return snap;
        foreach (var line in res.Stdout.Split('\n'))
        {
            if (line.Length == 0) continue;
            var f = line.Split('\t');
            if (f.Length < 5) continue;
            var full = f[0];
            if (full.StartsWith("refs/heads/"))
            {
                snap.Branches.Add(new BranchInfo
                {
                    Name = full["refs/heads/".Length..], FullRef = full, Sha = f[1], IsCurrent = f[2] == "*",
                    Upstream = f[3].Length > 0 ? f[3] : null, Track = f[4],
                });
            }
            else if (full.StartsWith("refs/remotes/"))
            {
                var name = full["refs/remotes/".Length..];
                if (name.EndsWith("/HEAD")) continue;
                snap.Branches.Add(new BranchInfo { Name = name, FullRef = full, Sha = f[1], IsRemote = true });
                snap.RemoteBranchNames.Add(name);
            }
            else if (full.StartsWith("refs/tags/"))
            {
                snap.Tags.Add(new TagInfo { Name = full["refs/tags/".Length..], Sha = f[1] });
            }
        }
        return snap;
    }

    public async Task<List<StashInfo>> StashesAsync()
    {
        var res = await Q("stash list --format=%gd%x09%s");
        var list = new List<StashInfo>();
        if (!res.Ok) return list;
        foreach (var line in res.Stdout.Split('\n'))
        {
            var i = line.IndexOf('\t');
            if (i <= 0) continue;
            list.Add(new StashInfo { Id = line[..i], Message = line[(i + 1)..] });
        }
        return list;
    }

    public async Task<List<string>> RemotesAsync()
    {
        var res = await Q("remote");
        return res.Ok ? res.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList() : new List<string>();
    }

    public async Task<List<CommitInfo>> LogAsync(int max, string logScope, RefSnapshot refs)
    {
        // "local" shows local branches plus only the remote branches they track, so remote-only branches stay out of the graph.
        var upstreams = refs.Branches.Where(b => !b.IsRemote && b.Upstream != null && refs.RemoteBranchNames.Contains(b.Upstream!))
                                     .Select(b => "refs/remotes/" + b.Upstream);
        var scope = logScope switch
        {
            "all" => "--branches --remotes --tags HEAD",
            "head" => "HEAD",
            _ => string.Join(" ", new[] { "--branches --tags HEAD" }.Concat(upstreams)),
        };
        var res = await Q($"log --date-order -n {max} --format=%H%x09%P%x09%an%x09%ae%x09%aI%x09%D%x09%s {scope}");
        var list = new List<CommitInfo>();
        if (!res.Ok) return list;
        foreach (var line in res.Stdout.Split('\n'))
        {
            if (line.Length == 0) continue;
            var f = line.Split('\t', 7);
            if (f.Length < 7) continue;
            DateTimeOffset.TryParse(f[4], null, System.Globalization.DateTimeStyles.AssumeUniversal, out var date);
            var c = new CommitInfo
            {
                Hash = f[0],
                Parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                Author = f[2], Email = f[3], Date = date, Subject = f[6],
            };
            foreach (var r in f[5].Split(", ", StringSplitOptions.RemoveEmptyEntries))
            {
                var name = r.Trim();
                if (name.Length == 0) continue;
                if (name.StartsWith("HEAD -> ")) { c.Refs.Insert(0, new RefBadge(name[8..], RefKind.Head)); continue; }
                if (name == "HEAD") { c.Refs.Insert(0, new RefBadge("HEAD", RefKind.Head)); continue; }
                if (name.StartsWith("tag: ")) { c.Refs.Add(new RefBadge(name[5..], RefKind.Tag)); continue; }
                if (name.EndsWith("/HEAD")) continue;
                c.Refs.Add(new RefBadge(name, refs.RemoteBranchNames.Contains(name) ? RefKind.RemoteBranch : RefKind.LocalBranch));
            }
            list.Add(c);
        }
        return list;
    }

    // ---------------- file history / blame ----------------

    private const string HistoryFormat = "--format=%x1e%H%x09%P%x09%an%x09%ae%x09%aI%x09%s";

    /// <summary>Parses the HistoryFormat line: hash, parents, author, email, date, subject.</summary>
    private static CommitInfo? ParseHistoryCommit(string line)
    {
        var f = line.Split('\t', 6);
        if (f.Length < 6) return null;
        DateTimeOffset.TryParse(f[4], null, System.Globalization.DateTimeStyles.AssumeUniversal, out var date);
        return new CommitInfo
        {
            Hash = f[0], Parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
            Author = f[2], Email = f[3], Date = date, Subject = f[5],
        };
    }

    /// <summary>
    /// Commits that touched <paramref name="path"/> up to <paramref name="rev"/> (HEAD if null). Following renames is much
    /// slower in big repos (git can't use its changed-path filters), so callers show the plain history first.
    /// </summary>
    public async Task<List<HistoryEntry>> FileHistoryAsync(string path, string? rev, bool follow, CancellationToken ct, int max = 1000)
    {
        var res = await BackgroundQ($"log {(follow ? "--follow -M " : "")}-n {max} {HistoryFormat} --name-only {rev ?? "HEAD"} -- {Quote(path)}", ct);
        var list = new List<HistoryEntry>();
        if (!res.Ok) return list;
        foreach (var chunk in res.Stdout.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || ParseHistoryCommit(lines[0]) is not { } c) continue;
            list.Add(new HistoryEntry { Commit = c, Path = lines.Length > 1 ? lines[^1].Trim() : path });
        }
        // The next-older entry's path is where a rename came from.
        for (int i = 0; i + 1 < list.Count; i++)
            if (list[i + 1].Path != list[i].Path) list[i].PrevPath = list[i + 1].Path;
        return list;
    }

    /// <summary>Commits that changed lines <paramref name="start"/>–<paramref name="end"/> of the file as it is in <paramref name="rev"/>, each with a patch of just those lines.</summary>
    public async Task<List<HistoryEntry>> LineHistoryAsync(string rev, string path, int start, int end, CancellationToken ct)
    {
        var res = await BackgroundQ($"log -L {start},{end}:{Quote(path)} {HistoryFormat} --no-color {rev}", ct);
        var list = new List<HistoryEntry>();
        if (!res.Ok) return list;
        foreach (var chunk in res.Stdout.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var nl = chunk.IndexOf('\n');
            if (ParseHistoryCommit(nl < 0 ? chunk : chunk[..nl]) is not { } c) continue;
            var patch = nl < 0 ? "" : chunk[(nl + 1)..].TrimStart('\n');
            // The patch's "+++ b/<path>" names the file in this commit, which differs before a rename.
            var m = System.Text.RegularExpressions.Regex.Match(patch, @"^\+\+\+ b/(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline);
            var p = System.Text.RegularExpressions.Regex.Match(patch, @"^--- a/(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline);
            var at = m.Success ? m.Groups[1].Value.TrimEnd() : path;
            list.Add(new HistoryEntry { Commit = c, Path = at, PrevPath = p.Success && p.Groups[1].Value.TrimEnd() != at ? p.Groups[1].Value.TrimEnd() : null, LinePatch = patch });
        }
        return list;
    }

    /// <summary>Blame of <paramref name="path"/> at <paramref name="rev"/>, or of the working-tree file when rev is null.</summary>
    public async Task<(List<BlameLine> Lines, string? Error)> BlameAsync(string path, string? rev, CancellationToken ct)
    {
        var res = await BackgroundQ($"blame --porcelain {(rev != null ? rev + " " : "")}-- {Quote(path)}", ct);
        var list = new List<BlameLine>();
        if (!res.Ok) return (list, res.Stderr.Trim().Length > 0 ? res.Stderr.Trim() : "git blame failed");
        // Porcelain: "<sha> <orig> <final> [<count>]", then commit details the first time a sha appears, then "\t<line>".
        var info = new Dictionary<string, Dictionary<string, string>>();
        string sha = ""; int orig = 0, final = 0;
        string? prevSha = null;
        foreach (var raw in res.Stdout.Split('\n'))
        {
            if (raw.StartsWith('\t'))
            {
                var d = info[sha];
                d.TryGetValue("previous", out var prev);
                var pv = prev?.Split(' ', 2);
                long.TryParse(d.GetValueOrDefault("author-time"), out var t);
                var line = new BlameLine
                {
                    LineNo = final, OrigLineNo = orig, Hash = sha,
                    Author = d.GetValueOrDefault("author") ?? "", Date = DateTimeOffset.FromUnixTimeSeconds(t),
                    Summary = d.GetValueOrDefault("summary") ?? "", FileName = d.GetValueOrDefault("filename") ?? path,
                    PrevHash = pv?[0], PrevFileName = pv?.Length > 1 ? pv[1] : null,
                    Text = raw[1..].TrimEnd('\r').Replace("\t", "    "),
                    FirstInRun = sha != prevSha,
                };
                list.Add(line);
                prevSha = sha;
                continue;
            }
            var sp = raw.IndexOf(' ');
            var key = sp < 0 ? raw : raw[..sp];
            if (key.Length == 40 && key.All(Uri.IsHexDigit))
            {
                var parts = raw.Split(' ');
                sha = key; int.TryParse(parts[1], out orig); int.TryParse(parts[2], out final);
                if (!info.ContainsKey(sha)) info[sha] = new();
            }
            else if (key.Length > 0 && sha.Length > 0)
                info[sha][key] = sp < 0 ? "" : raw[(sp + 1)..];
        }
        return (list, null);
    }

    public async Task<string> CommitBodyAsync(string hash)
    {
        var res = await Q($"log -1 --format=%B {hash}");
        return res.Ok ? res.Stdout.TrimEnd() : "";
    }

    public async Task<List<FileStatus>> CommitFilesAsync(CommitInfo c)
    {
        var parent = c.Parents.Length > 0 ? c.Parents[0] : EmptyTree;
        var res = await Q($"diff --name-status -z -M {parent} {c.Hash}");
        var list = new List<FileStatus>();
        if (!res.Ok) return list;
        var t = res.Stdout.Split('\0');
        for (int i = 0; i + 1 < t.Length; i++)
        {
            var code = t[i];
            if (code.Length == 0) continue;
            char x = code[0];
            if ((x == 'R' || x == 'C') && i + 2 < t.Length)
            {
                list.Add(new FileStatus { X = x, OldPath = t[i + 1], Path = t[i + 2] });
                i += 2;
            }
            else
            {
                list.Add(new FileStatus { X = x, Path = t[i + 1] });
                i += 1;
            }
        }
        return list;
    }

    /// <summary>
    /// Plain diffs of LFS-tracked files only compare the pointer files (oid/size). When that is what came back, diff again
    /// with a cat textconv so git smudges both sides and the real content shows. Binary objects keep the pointer diff.
    /// The result is never a patch git apply can use against the index (which holds pointers), so IsLfs means read-only.
    /// </summary>
    private const string LfsPointerMarker = "version https://git-lfs.github.com/spec/v1";

    private async Task<(string Text, bool IsLfs)> LfsAwareDiffAsync(string args)
    {
        var res = await Q(args);
        if (!res.Stdout.Contains(LfsPointerMarker)) return (res.Stdout, false);
        var conv = await Q("-c diff.lfs.textconv=cat " + args);
        if (!conv.Ok || LooksBinary(conv.Stdout)) return (res.Stdout, true);
        return (conv.Stdout, true);
    }

    /// <summary>NUL, or invalid UTF-8 (decoded as U+FFFD), in the first 8000 chars. Textconv output skips git's own binary check.</summary>
    private static bool LooksBinary(string s) => s.AsSpan(0, Math.Min(s.Length, 8000)).IndexOfAny('\0', '�') >= 0;

    public async Task<(string Text, bool IsLfs)> DiffAsync(FileStatus f, bool staged)
    {
        if (!staged && f.Untracked)
        {
            var full = Path.Combine(Root, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(full))
            {
                var files = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Take(500)
                    .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/'));
                return ("Untracked directory. Files:\n" + string.Join("\n", files.Select(x => "+ " + x)), false);
            }
            return (await UntrackedAsDiffAsync(full, f.Path), false);
        }
        var paths = f.OldPath != null ? Quote(f.OldPath) + " " + Quote(f.Path) : Quote(f.Path);
        var args = staged ? $"diff --cached -M --no-color -- {paths}" : $"diff -M --no-color -- {paths}";
        return await LfsAwareDiffAsync(args);
    }

    public async Task<string> DiffCommitFileAsync(CommitInfo c, FileStatus f)
    {
        var parent = c.Parents.Length > 0 ? c.Parents[0] : EmptyTree;
        var paths = f.OldPath != null ? Quote(f.OldPath) + " " + Quote(f.Path) : Quote(f.Path);
        return (await LfsAwareDiffAsync($"diff -M --no-color {parent} {c.Hash} -- {paths}")).Text;
    }

    public async Task<string> StashPatchAsync(string stashId)
    {
        var args = $"stash show -p --include-untracked {stashId}";
        var res = await Q(args);
        if (!res.Ok || res.Stdout.Length == 0)
            res = await Q(args = $"stash show -p {stashId}");
        if (!res.Stdout.Contains(LfsPointerMarker)) return res.Stdout;

        // Same as LfsAwareDiffAsync, but per file: a stash can mix text and binary LFS objects with ordinary files.
        // stash show ignores textconv unless asked for it explicitly.
        var conv = await Q("-c diff.lfs.textconv=cat " + args.Replace("stash show -p", "stash show -p --textconv"));
        if (!conv.Ok) return res.Stdout;
        var plain = SplitFileDiffs(res.Stdout).ToDictionary(d => d.Header, d => d.Text);
        var sb = new System.Text.StringBuilder();
        foreach (var (header, text) in SplitFileDiffs(conv.Stdout))
            sb.Append(LooksBinary(text) && plain.TryGetValue(header, out var p) ? p : text);
        return sb.ToString();
    }

    /// <summary>Splits multi-file diff output at each "diff --git" line, keyed by that line.</summary>
    private static List<(string Header, string Text)> SplitFileDiffs(string diff)
    {
        var list = new List<(string, string)>();
        int start = 0;
        while (start < diff.Length)
        {
            int next = diff.IndexOf("\ndiff --git ", start, StringComparison.Ordinal);
            int end = next < 0 ? diff.Length : next + 1;
            var text = diff[start..end];
            int nl = text.IndexOf('\n');
            var header = nl < 0 ? text : text[..nl];
            // Duplicate headers can't happen within one diff, but keep the first if they ever do.
            if (!list.Any(x => x.Item1 == header)) list.Add((header, text));
            start = end;
        }
        return list;
    }

    private static async Task<string> UntrackedAsDiffAsync(string fullPath, string relPath)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists) return "";
            if (fi.Length > 2_000_000) return $"diff --git a/{relPath} b/{relPath}\n(file too large to preview: {fi.Length / 1024} KB)";
            var bytes = await File.ReadAllBytesAsync(fullPath);
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0)
                return $"diff --git a/{relPath} b/{relPath}\nBinary file ({fi.Length} bytes)";
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            var lines = text.Split('\n');
            if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
            var sb = new System.Text.StringBuilder();
            sb.Append($"diff --git a/{relPath} b/{relPath}\nnew file mode 100644\n--- /dev/null\n+++ b/{relPath}\n@@ -0,0 +1,{lines.Length} @@\n");
            foreach (var l in lines) sb.Append('+').Append(l.TrimEnd('\r')).Append('\n');
            return sb.ToString();
        }
        catch (Exception ex) { return "Could not read file: " + ex.Message; }
    }

    public RepoOperation GetOperation()
    {
        if (File.Exists(Path.Combine(GitDir, "MERGE_HEAD"))) return RepoOperation.Merge;
        if (Directory.Exists(Path.Combine(GitDir, "rebase-merge")) || Directory.Exists(Path.Combine(GitDir, "rebase-apply"))) return RepoOperation.Rebase;
        if (File.Exists(Path.Combine(GitDir, "CHERRY_PICK_HEAD"))) return RepoOperation.CherryPick;
        if (File.Exists(Path.Combine(GitDir, "REVERT_HEAD"))) return RepoOperation.Revert;
        return RepoOperation.None;
    }

    // ---------------- actions ----------------

    public Task<CommandResult> StageAsync(IEnumerable<string> paths) => Run($"add -A -- {QuoteAll(paths)}");
    public Task<CommandResult> StageAllAsync() => Run("add -A");
    public Task<CommandResult> UnstageAsync(IEnumerable<string> paths) => Run($"reset -q -- {QuoteAll(paths)}");
    public Task<CommandResult> UnstageAllAsync() => Run("reset -q");
    public Task<CommandResult> DiscardAsync(IEnumerable<string> paths) => Run($"checkout -- {QuoteAll(paths)}");
    public Task<CommandResult> CleanAsync(IEnumerable<string> paths) => Run($"clean -fd -- {QuoteAll(paths)}");
    public Task<CommandResult> ApplyPatchAsync(string patch, bool cached, bool reverse)
        => Run($"apply --whitespace=nowarn{(cached ? " --cached" : "")}{(reverse ? " -R" : "")} -", patch);

    public Task<CommandResult> CommitAsync(string message, bool amend)
        => Run($"commit{(amend ? " --amend" : "")} -F -", message.Replace("\r\n", "\n").TrimEnd() + "\n");

    public Task<CommandResult> FetchAsync() => Run("fetch --all --prune");
    // --autostash: git stashes local changes, pulls, then re-applies them; a conflicting re-apply keeps the stash.
    public Task<CommandResult> PullAsync(string mode) => Run(mode switch { "rebase" => "pull --rebase", "ff" => "pull --ff-only", _ => "pull" } + " --autostash");
    public Task<CommandResult> PushAsync(string extra = "") => Run(("push " + extra).Trim());
    public Task<CommandResult> PushNewBranchAsync(string remote, string branch) => Run($"push -u {remote} {branch}");

    public Task<CommandResult> CheckoutAsync(string target) => Run($"checkout {Quote(target)}");
    public Task<CommandResult> CheckoutRemoteAsync(string remoteBranch)
    {
        var local = remoteBranch.Contains('/') ? remoteBranch[(remoteBranch.IndexOf('/') + 1)..] : remoteBranch;
        return Run($"checkout -b {Quote(local)} --track {Quote(remoteBranch)}");
    }
    public Task<CommandResult> CreateBranchAsync(string name, string? at, bool checkout)
        => Run(checkout ? $"checkout -b {Quote(name)}{(at != null ? " " + at : "")}" : $"branch {Quote(name)}{(at != null ? " " + at : "")}");
    public Task<CommandResult> DeleteBranchAsync(string name, bool force) => Run($"branch {(force ? "-D" : "-d")} {Quote(name)}");
    public Task<CommandResult> RenameBranchAsync(string oldName, string newName) => Run($"branch -m {Quote(oldName)} {Quote(newName)}");
    public Task<CommandResult> DeleteRemoteBranchAsync(string remote, string branch) => Run($"push {remote} --delete {Quote(branch)}");
    public Task<CommandResult> SetUpstreamAsync(string branch, string upstream) => Run($"branch --set-upstream-to={Quote(upstream)} {Quote(branch)}");

    public Task<CommandResult> MergeAsync(string branch) => Run($"merge {Quote(branch)}");
    public Task<CommandResult> RebaseAsync(string onto) => Run($"rebase {Quote(onto)}");
    public Task<CommandResult> CherryPickAsync(string hash) => Run($"cherry-pick {hash}");
    public Task<CommandResult> RevertAsync(string hash) => Run($"-c core.editor=true revert {hash}");
    public Task<CommandResult> ResetAsync(string mode, string target) => Run($"reset --{mode} {target}");

    public Task<CommandResult> CreateTagAsync(string name, string? at, string? message)
        => message is { Length: > 0 } ? Run($"tag -a {Quote(name)} -F -{(at != null ? " " + at : "")}", message + "\n")
                                      : Run($"tag {Quote(name)}{(at != null ? " " + at : "")}");
    public Task<CommandResult> DeleteTagAsync(string name) => Run($"tag -d {Quote(name)}");
    public Task<CommandResult> PushTagAsync(string remote, string name) => Run($"push {remote} {Quote("refs/tags/" + name)}");

    public Task<CommandResult> StashPushAsync(string? message, bool includeUntracked)
        => Run($"stash push{(includeUntracked ? " -u" : "")}{(message is { Length: > 0 } ? " -m " + Quote(message) : "")}");
    public Task<CommandResult> StashApplyAsync(string id) => Run($"stash apply {id}");
    public Task<CommandResult> StashPopAsync(string id) => Run($"stash pop {id}");
    public Task<CommandResult> StashDropAsync(string id) => Run($"stash drop {id}");

    public Task<CommandResult> ResolveAsync(string path, string side) => Run($"checkout --{side} -- {Quote(path)}");

    public Task<CommandResult> OperationContinueAsync(RepoOperation op) => op switch
    {
        RepoOperation.Merge => Run("-c core.editor=true commit --no-edit"),
        RepoOperation.Rebase => Run("-c core.editor=true rebase --continue"),
        RepoOperation.CherryPick => Run("-c core.editor=true cherry-pick --continue"),
        RepoOperation.Revert => Run("-c core.editor=true revert --continue"),
        _ => Task.FromResult(new CommandResult(0, "", "", false)),
    };
    public Task<CommandResult> OperationAbortAsync(RepoOperation op) => op switch
    {
        RepoOperation.Merge => Run("merge --abort"),
        RepoOperation.Rebase => Run("rebase --abort"),
        RepoOperation.CherryPick => Run("cherry-pick --abort"),
        RepoOperation.Revert => Run("revert --abort"),
        _ => Task.FromResult(new CommandResult(0, "", "", false)),
    };
    public Task<CommandResult> OperationSkipAsync(RepoOperation op) => op switch
    {
        RepoOperation.Rebase => Run("rebase --skip"),
        RepoOperation.CherryPick => Run("cherry-pick --skip"),
        RepoOperation.Revert => Run("revert --skip"),
        _ => Task.FromResult(new CommandResult(0, "", "", false)),
    };
}
