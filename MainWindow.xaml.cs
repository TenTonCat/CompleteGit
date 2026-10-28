using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using CompleteGit.Controls;
using CompleteGit.Git;
using CompleteGit.Shell;
using Microsoft.Win32;

namespace CompleteGit;

public partial class MainWindow : Window
{
    private readonly CommandRunner _runner = new();
    private readonly Settings _settings;
    private GitRepo? _repo;
    private HeadInfo _head = new();
    private RefSnapshot _refs = new();
    private List<StashInfo> _stashes = new();
    private List<FileStatus> _status = new();
    private List<CommitInfo> _commits = new();
    private readonly CommitInfo _workRow = new() { IsWorkingTree = true, Subject = "Working tree" };
    private RepoOperation _op;
    private bool _refreshing, _refreshQueued, _syncingSelection;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _watchTimer;
    private int _diffSerial, _commitSerial, _logLimit = 1500;
    /// <summary>Fetches whatever the diff panel is showing again (after the whole-file toggle changes).</summary>
    private Func<Task>? _reloadDiff;
    /// <summary>File list the diff panel's file came from (for change navigation into the next file); null for history, blame and stashes.</summary>
    private ListBox? _diffList;
    private readonly HashSet<string> _collapsed = new() { "tags", "remotes" };
    // Stashes can be multi-selected (Ctrl/Shift+click) so many can be dropped at once
    private readonly List<TreeViewItem> _stashNodes = new();
    private readonly HashSet<string> _multiStash = new();
    private string? _stashAnchor;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _settings = Settings.Load();
        if (_settings.WindowWidth is > 300) Width = _settings.WindowWidth.Value;
        if (_settings.WindowHeight is > 300) Height = _settings.WindowHeight.Value;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        ApplyLogScope();

        _runner.Shell = _settings.Shell;
        _runner.GitExe = _settings.GitPath;
        ConsoleView.Shell = _settings.Shell;
        _runner.Output += (k, t) => ConsoleView.Append(k, t);
        _runner.BusyChanged += b => Dispatcher.BeginInvoke(() => SetBusy(b));
        _runner.ConsoleDirectoryChanged += d => Dispatcher.BeginInvoke(() => ConsoleView.SetPrompt(d));
        ConsoleView.CommandSubmitted += line => _ = RunUserCommandAsync(line);
        ConsoleView.StopRequested += () => _runner.Cancel();
        ConsoleView.ShellChanged += s => { _runner.Shell = s; _settings.Shell = s; _settings.Save(); };
        ConsoleView.QueriesVisible = _settings.ShowQueries;
        ConsoleView.ShowQueriesChanged += v => { _settings.ShowQueries = v; _settings.Save(); };
        ConsoleView.SetPrompt(_runner.ConsoleDirectory);
        Diff.HunkRequested += (op, patch) => _ = ApplyHunkAsync(op, patch);
        Diff.FileEdited += () => _ = ShowWorkDiffAsync();
        Diff.LineHistoryRequested += (rev, path, start, end) => _ = ShowLineHistoryAsync(rev, path, start, end);
        Diff.BlameRequested += (rev, path, line) => _ = ShowBlameAsync(path, rev, line);
        Diff.SideBySide = _settings.SideBySide;
        Diff.SideBySideChanged += v => { _settings.SideBySide = v; _settings.Save(); };
        Diff.StepFile = StepDiffFile;
        Diff.WholeFile = _settings.WholeFile;
        Diff.WholeFileChanged += v =>
        {
            _settings.WholeFile = v; _settings.Save();
            if (_repo != null) _repo.WholeFile = v;
            if (Diff.ShowsDiff) _ = _reloadDiff?.Invoke();
        };
        ApplyPullMode();
        ApplyPushMode();
        UpdateFileHeaders();
        if (_settings.CommitBoxHeight is double ch) CommitRow.Height = new GridLength(Math.Max(ch, CommitRow.MinHeight));

        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _watchTimer.Tick += (_, _) =>
        {
            _watchTimer.Stop();
            if (_runner.IsBusy) _watchTimer.Start(); else _ = RefreshAsync();
        };

        Loaded += async (_, _) =>
        {
            LoadDockLayout();
            ConsoleView.Append(OutputKind.Info, "CompleteGit ready. Every git command the UI runs is echoed here; type your own commands below.");
            var args = Environment.GetCommandLineArgs();
            var start = args.Length > 1 && Directory.Exists(args[1]) ? Path.GetFullPath(args[1]) : _settings.LastRepo;
            if (start == null || !Directory.Exists(start)) start = Environment.CurrentDirectory;
            if (!await OpenRepoAsync(start, silent: true))
                ConsoleView.Append(OutputKind.Info, "No repository open. Use Repository ▾ → Open… (Ctrl+O).");
        };
        Closing += (_, _) => SaveLayout();
    }

    public void ShowError(string text) => _ = Dialogs.InfoAsync("Unexpected error", text);

    // ------------------------------------------------------------------ repo lifecycle

    private async Task<bool> OpenRepoAsync(string dir, bool silent = false)
    {
        var repo = await GitRepo.OpenAsync(_runner, dir);
        if (repo == null)
        {
            if (!silent) await Dialogs.InfoAsync("Not a repository", $"'{dir}' is not inside a git repository.");
            return false;
        }
        _repo = repo;
        _repo.WholeFile = _settings.WholeFile;
        _runner.ConsoleDirectory = repo.Root;
        ConsoleView.SetPrompt(repo.Root);
        Title = $"CompleteGit — {repo.Root}";
        StatusRepo.Text = repo.Root;
        _settings.LastRepo = repo.Root;
        _settings.AddRecent(repo.Root);
        _settings.Save();
        CommitMessage.Clear();
        AmendCheck.IsChecked = false;
        Diff.Clear();
        SetupWatcher(repo.Root);
        await RefreshAsync();
        return true;
    }

    private void SetupWatcher(string root)
    {
        _watcher?.Dispose();
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler h = (_, e) => OnFsEvent(e.FullPath, e.Name);
            _watcher.Changed += h; _watcher.Created += h; _watcher.Deleted += h;
            _watcher.Renamed += (_, e) => OnFsEvent(e.FullPath, e.Name);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { ConsoleView.Append(OutputKind.Error, "File watcher unavailable: " + ex.Message); }
    }

    private void OnFsEvent(string path, string? name)
    {
        // Ignored paths (Unity's Temp/ and Library/, build output) churn constantly and never change what git shows.
        if (name != null && _repo?.IsIgnored(name) == true) return;
        if (path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return;
        var gitSeg = Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar;
        var i = path.IndexOf(gitSeg, StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            var inner = path[(i + gitSeg.Length)..];
            // lfs: git status runs the LFS filter, which churns .git\lfs\tmp — watching it makes every refresh trigger the next.
            if (inner.StartsWith("objects", StringComparison.OrdinalIgnoreCase) || inner.StartsWith("logs", StringComparison.OrdinalIgnoreCase)
                || inner.StartsWith("lfs", StringComparison.OrdinalIgnoreCase)) return;
        }
        Dispatcher.BeginInvoke(() => { _watchTimer.Stop(); _watchTimer.Start(); });
    }

    private async Task RefreshAsync()
    {
        if (_repo == null) return;
        if (_refreshing) { _refreshQueued = true; return; }
        _refreshing = true;
        try
        {
            var repo = _repo;
            var head = await repo.HeadAsync();
            var status = await repo.StatusAsync();
            var refs = await repo.RefsAsync();
            var stashes = await repo.StashesAsync();
            var op = repo.GetOperation();
            var commits = await repo.LogAsync(_logLimit, _settings.LogScope, refs);
            if (!ReferenceEquals(_repo, repo)) return;
            _head = head; _status = status; _refs = refs; _stashes = stashes; _op = op;
            ApplyHead();
            ApplyStatus();
            BuildRefTree();
            ApplyLog(commits);
            ApplyOperation();
        }
        catch (Exception ex) { ConsoleView.Append(OutputKind.Error, "Refresh failed: " + ex.Message); }
        finally
        {
            _refreshing = false;
            if (_refreshQueued) { _refreshQueued = false; _ = RefreshAsync(); }
        }
    }

    private void ApplyHead()
    {
        HeadLabel.Text = _head.Display;
        var cur = _refs.Current;
        TrackLabel.Text = cur?.Upstream != null ? $"{cur.Upstream} {cur.Track}".Trim() : (_head.Detached ? "" : "no upstream");
    }

    private void ApplyStatus()
    {
        var unstaged = SortEntries(_status.Where(s => s.HasUnstaged).Select(s => new ChangeEntry(s, false)));
        var staged = SortEntries(_status.Where(s => s.HasStaged).Select(s => new ChangeEntry(s, true)));
        var selU = UnstagedList.SelectedItems.OfType<ChangeEntry>().Select(e => e.Path).ToHashSet();
        var selS = StagedList.SelectedItems.OfType<ChangeEntry>().Select(e => e.Path).ToHashSet();

        _syncingSelection = true;
        UnstagedList.ItemsSource = unstaged;
        StagedList.ItemsSource = staged;
        foreach (var e in unstaged.Where(e => selU.Contains(e.Path))) UnstagedList.SelectedItems.Add(e);
        foreach (var e in staged.Where(e => selS.Contains(e.Path))) StagedList.SelectedItems.Add(e);
        if (UnstagedList.SelectedItems.Count == 0 && StagedList.SelectedItems.Count == 0)
        {
            if (unstaged.Count > 0) { UnstagedList.SelectedIndex = 0; _activeFileList = UnstagedList; }
            else if (staged.Count > 0) { StagedList.SelectedIndex = 0; _activeFileList = StagedList; }
        }
        _syncingSelection = false;

        UnstagedHeader.Text = $"Unstaged ({unstaged.Count})";
        StagedHeader.Text = $"Staged ({staged.Count})";
        StatusCounts.Text = $"{unstaged.Count} unstaged · {staged.Count} staged";
        int conflicts = _status.Count(s => s.Conflicted);
        _workRow.Subject = _status.Count == 0 ? "Working tree — clean"
            : $"Working tree — {_status.Count} change{(_status.Count == 1 ? "" : "s")}{(conflicts > 0 ? $", {conflicts} conflict{(conflicts == 1 ? "" : "s")}" : "")}";

        if (LogList.SelectedItem is CommitInfo { IsWorkingTree: true } || LogList.SelectedItem == null)
            _ = ShowWorkDiffAsync();
    }

    private void ApplyLog(List<CommitInfo> commits)
    {
        int lanes = GraphBuilder.Build(commits);
        GraphColumn.Width = Math.Min(lanes, 24) * GraphCell.LaneWidth + 12;
        _commits = new List<CommitInfo>(commits.Count + 1) { _workRow };
        _commits.AddRange(commits);
        ApplyLogFilter();
    }

    private void ApplyLogFilter()
    {
        var selectedHash = (LogList.SelectedItem as CommitInfo)?.Hash;
        bool wasWork = LogList.SelectedItem is CommitInfo { IsWorkingTree: true } || LogList.SelectedItem == null;
        var f = LogFilter.Text.Trim();
        IEnumerable<CommitInfo> items = _commits;
        if (f.Length > 0)
            items = _commits.Where(c => c.IsWorkingTree
                || c.Subject.Contains(f, StringComparison.OrdinalIgnoreCase)
                || c.Author.Contains(f, StringComparison.OrdinalIgnoreCase)
                || c.Hash.StartsWith(f, StringComparison.OrdinalIgnoreCase)
                || c.Refs.Any(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)));
        var list = items.ToList();
        list[0] = _workRow;
        LogList.ItemsSource = null;
        LogList.ItemsSource = list;
        CommitInfo? sel = wasWork ? _workRow : list.FirstOrDefault(c => c.Hash == selectedHash);
        _syncingSelection = true;
        LogList.SelectedItem = sel ?? _workRow;
        _syncingSelection = false;
        if (sel is not { IsWorkingTree: false }) ShowWorkPanel();
    }

    private void ApplyOperation()
    {
        if (_op == RepoOperation.None) { OpBanner.Visibility = Visibility.Collapsed; return; }
        OpBanner.Visibility = Visibility.Visible;
        BtnOpSkip.Visibility = _op == RepoOperation.Merge ? Visibility.Collapsed : Visibility.Visible;
        int conflicts = _status.Count(s => s.Conflicted);
        OpBannerText.Text = _op switch
        {
            RepoOperation.Merge => "Merge in progress",
            RepoOperation.Rebase => "Rebase in progress",
            RepoOperation.CherryPick => "Cherry-pick in progress",
            _ => "Revert in progress",
        } + (conflicts > 0 ? $" — {conflicts} conflicted file(s): resolve, stage, then Continue" : " — all conflicts resolved, press Continue");
    }

    private void SetBusy(bool busy)
    {
        ConsoleView.SetBusy(busy);
        StatusBusy.Text = busy ? "● running" : "";
    }

    private void SaveLayout()
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { _settings.WindowWidth = Width; _settings.WindowHeight = Height; }
        if (CommitRow.ActualHeight > 0) _settings.CommitBoxHeight = CommitRow.ActualHeight;
        _settings.Save();
        try { new XmlLayoutSerializer(Dock).Serialize(Settings.LayoutPath); } catch { /* non-fatal */ }
    }

    // ------------------------------------------------------------------ dockable panels

    private string? _defaultLayout;

    /// <summary>Remembers the XAML-defined layout (for Reset) and restores the saved arrangement from layout.xml.</summary>
    private void LoadDockLayout()
    {
        using (var sw = new StringWriter()) { new XmlLayoutSerializer(Dock).Serialize(sw); _defaultLayout = sw.ToString(); }
        if (!File.Exists(Settings.LayoutPath)) return;
        try { RestoreDockLayout(File.ReadAllText(Settings.LayoutPath)); }
        catch (Exception ex) { ConsoleView.Append(OutputKind.Error, "Could not restore the panel layout, using the default: " + ex.Message); }
    }

    private void RestoreDockLayout(string xml)
    {
        var panels = Dock.Layout.Descendents().OfType<LayoutAnchorable>().Where(a => a.ContentId != null).ToDictionary(a => a.ContentId!, a => (a.Title, a.Content));
        // A panel that shares a tab group in the current layout (e.g. History beside Log) remembers that neighbour.
        var neighbour = Dock.Layout.Descendents().OfType<LayoutAnchorable>().Where(a => a.ContentId != null)
            .ToDictionary(a => a.ContentId!, a => (a.Parent as LayoutAnchorablePane)?.Children.OfType<LayoutAnchorable>().Select(x => x.ContentId).FirstOrDefault(id => id != a.ContentId));
        var ser = new XmlLayoutSerializer(Dock);
        // Content is matched by ContentId; drop anything in the file that no longer exists in this version.
        ser.LayoutSerializationCallback += (_, e) => { if (e.Content == null) e.Cancel = true; };
        using (var sr = new StringReader(xml)) ser.Deserialize(sr);
        // Panels added since the layout was saved would otherwise vanish: tab each one beside its neighbour, or dock it at the bottom.
        var present = Dock.Layout.Descendents().OfType<LayoutAnchorable>().ToDictionary(a => a.ContentId ?? "", a => a);
        foreach (var (id, (title, content)) in panels.Where(p => !present.ContainsKey(p.Key)))
        {
            var panel = new LayoutAnchorable { Title = title, ContentId = id, Content = content, CanClose = false };
            if (neighbour[id] is { } n && present.TryGetValue(n, out var near) && near.Parent is LayoutAnchorablePane pane)
                pane.Children.Add(panel);
            else
                Dock.Layout.RootPanel.Children.Add(new LayoutAnchorablePane(panel) { DockHeight = new GridLength(220) });
        }
    }

    private IEnumerable<LayoutAnchorable> Panels => Dock.Layout.Descendents().OfType<LayoutAnchorable>().Where(a => a.ContentId != null);

    /// <summary>Makes a hidden or pinned-away panel visible again and brings it to the front.</summary>
    private void ShowPanel(string contentId)
    {
        var a = Panels.FirstOrDefault(p => p.ContentId == contentId);
        if (a == null) return;
        if (a.IsHidden) a.Show();
        if (a.IsAutoHidden) a.ToggleAutoHide();
        a.IsActive = true;
    }

    private void PopulateViewMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        foreach (var a in Panels.OrderBy(p => p.Title))
        {
            var panel = a;
            var mi = new MenuItem { Header = panel.Title, IsChecked = !panel.IsHidden, ToolTip = panel.IsHidden ? "Show this panel" : "Hide this panel (drag its title bar to move it)" };
            mi.Click += (_, _) => { if (panel.IsHidden) ShowPanel(panel.ContentId); else panel.Hide(); };
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset layout", ToolTip = "Put every panel back where it started" };
        reset.Click += (_, _) => { if (_defaultLayout != null) RestoreDockLayout(_defaultLayout); };
        menu.Items.Add(reset);
    }

    // ------------------------------------------------------------------ console

    private async Task RunUserCommandAsync(string line)
    {
        await _runner.UserCommandAsync(line);
        await RefreshAsync();
    }

    /// <summary>Runs a git action, then refreshes. Errors are already in the console.</summary>
    private async Task DoAsync(Func<GitRepo, Task<CommandResult>> action)
    {
        if (_repo == null) { ConsoleView.Append(OutputKind.Error, "No repository open."); return; }
        try { await action(_repo); }
        catch (Exception ex) { ConsoleView.Append(OutputKind.Error, ex.Message); }
        await RefreshAsync();
    }

    private Task<bool> ConfirmAsync(string title, string message, string okText = "Yes", bool danger = false)
        => Dialogs.ConfirmAsync(title, message, okText, "Cancel", danger);

    // ------------------------------------------------------------------ sidebar

    private void BuildRefTree()
    {
        var selectedTag = (RefTree.SelectedItem as TreeViewItem)?.Tag;
        RefTree.Items.Clear();

        var local = Group("local", "Local branches", null);
        local.ContextMenu = Menu(("New branch…", () => CreateBranchAt(null)));
        AddBranchNodes(local, "local", _refs.Branches.Where(b => !b.IsRemote));
        RefTree.Items.Add(local);

        var remotes = Group("remotes", "Remote branches", null);
        remotes.ContextMenu = Menu(("Fetch all (prune)", () => _ = DoAsync(r => r.FetchAsync())));
        foreach (var remote in _refs.Branches.Where(b => b.IsRemote).GroupBy(b => b.Remote))
        {
            var key = "remotes/" + remote.Key;
            var node = Group(key, remote.Key, null);
            node.ContextMenu = Menu(("Fetch all (prune)", () => _ = DoAsync(r => r.FetchAsync())));
            AddBranchNodes(node, key, remote);
            remotes.Items.Add(node);
        }
        RefTree.Items.Add(remotes);

        var tags = Group("tags", $"Tags ({_refs.Tags.Count})", null);
        tags.ContextMenu = Menu(("New tag at HEAD…", () => CreateTagAt(null)));
        foreach (var t in _refs.Tags.OrderByDescending(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var it = Node("tags/" + t.Name, Header(t.Name, false, t.Sha), t);
            it.ContextMenu = Menu(
                ("Checkout tag (detached)", () => _ = DoAsync(r => r.CheckoutAsync(t.Name))),
                ("New branch from tag…", () => CreateBranchAt(t.Name)),
                null,
                ("Push tag", () => _ = PushTagAsync(t.Name)),
                ("Delete tag", async () => { if (await ConfirmAsync("Delete tag", $"Delete tag '{t.Name}'?", "Delete", true)) await DoAsync(r => r.DeleteTagAsync(t.Name)); }),
                null,
                ("Copy name", () => Clipboard.SetText(t.Name)));
            tags.Items.Add(it);
        }
        RefTree.Items.Add(tags);

        var stashes = Group("stashes", $"Stashes ({_stashes.Count})", null);
        stashes.ContextMenu = Menu(
            ("Stash changes…", () => StashPush_Click(this, new RoutedEventArgs())),
            null,
            ("Drop all stashes…", () => _ = DropStashesAsync(_stashes)));
        _stashNodes.Clear();
        _multiStash.IntersectWith(_stashes.Select(x => x.Id));
        foreach (var s in _stashes)
        {
            var it = Node("stashes/" + s.Id, Header($"{s.Id}: {s.Message}", false, null), s);
            it.ToolTip = "Ctrl+click or Shift+click to select several stashes, then drop them together (Delete)";
            var dropSelected = new MenuItem();
            dropSelected.Click += (_, _) => _ = DropStashesAsync(SelectedStashes());
            it.ContextMenu = Menu(
                ("Apply", () => _ = DoAsync(r => r.StashApplyAsync(s.Id))),
                ("Pop (apply and drop)", () => _ = DoAsync(r => r.StashPopAsync(s.Id))),
                ("Drop", () => _ = DropStashesAsync(new[] { s })),
                null,
                ("Copy message", () => Clipboard.SetText(s.Message)));
            it.ContextMenu.Items.Insert(3, dropSelected);
            it.ContextMenu.Opened += (_, _) =>
            {
                var n = SelectedStashes().Count;
                dropSelected.Visibility = n > 1 ? Visibility.Visible : Visibility.Collapsed;
                dropSelected.Header = $"Drop {n} selected stashes…";
            };
            _stashNodes.Add(it);
            stashes.Items.Add(it);
        }
        RefTree.Items.Add(stashes);
        UpdateStashHighlight();

        if (selectedTag != null) Reselect(RefTree.Items, selectedTag);
    }

    private void AddBranchNodes(TreeViewItem parent, string keyPrefix, IEnumerable<BranchInfo> branches)
    {
        var folders = new Dictionary<string, TreeViewItem>(StringComparer.Ordinal);
        foreach (var b in branches.OrderBy(b => b.ShortName, StringComparer.OrdinalIgnoreCase))
        {
            var parts = b.ShortName.Split('/');
            var container = parent;
            var key = keyPrefix;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                key += "/" + parts[i];
                if (!folders.TryGetValue(key, out var folder))
                {
                    folder = Group(key, parts[i], null);
                    folders[key] = folder;
                    container.Items.Add(folder);
                }
                container = folder;
            }
            var node = Node(key + "/" + parts[^1], Header(parts[^1], b.IsCurrent, b.Track.Length > 0 ? b.Track : null), b);
            node.ContextMenu = BranchMenuFor(b);
            container.Items.Add(node);
        }
    }

    private ContextMenu BranchMenuFor(BranchInfo b)
    {
        if (b.IsRemote)
        {
            return Menu(
                ("Checkout (create tracking branch)", () => _ = DoAsync(r => r.CheckoutRemoteAsync(b.Name))),
                ("Merge into current branch", () => _ = DoAsync(r => r.MergeAsync(b.Name))),
                ("Rebase current branch onto this", () => _ = DoAsync(r => r.RebaseAsync(b.Name))),
                ("New branch from here…", () => CreateBranchAt(b.Name)),
                null,
                ("Delete on remote", async () => { if (await ConfirmAsync("Delete remote branch", $"Delete '{b.ShortName}' on remote '{b.Remote}'?", "Delete", true)) await DoAsync(r => r.DeleteRemoteBranchAsync(b.Remote, b.ShortName)); }),
                null,
                ("Copy name", () => Clipboard.SetText(b.Name)));
        }
        return Menu(
            ("Checkout", () => _ = DoAsync(r => r.CheckoutAsync(b.Name))),
            ("Merge into current branch", () => _ = DoAsync(r => r.MergeAsync(b.Name))),
            ("Rebase current branch onto this", () => _ = DoAsync(r => r.RebaseAsync(b.Name))),
            ("New branch from here…", () => CreateBranchAt(b.Name)),
            null,
            ("Push", () => _ = PushBranchAsync(b)),
            ("Set upstream…", async () =>
            {
                var up = await Dialogs.AskAsync("Set upstream", $"Upstream for '{b.Name}'", b.Upstream ?? $"origin/{b.Name}", "Set");
                if (up is { Length: > 0 }) await DoAsync(r => r.SetUpstreamAsync(b.Name, up.Trim()));
            }),
            ("Rename…", async () =>
            {
                var n = await Dialogs.AskAsync("Rename branch", "New name", b.Name, "Rename");
                if (n is { Length: > 0 } && n.Trim() != b.Name) await DoAsync(r => r.RenameBranchAsync(b.Name, n.Trim()));
            }),
            ("Delete", async () => { if (await ConfirmAsync("Delete branch", $"Delete branch '{b.Name}'?", "Delete", true)) await DoAsync(r => r.DeleteBranchAsync(b.Name, false)); }),
            ("Delete (force, even if unmerged)", async () => { if (await ConfirmAsync("Force-delete branch", $"Force-delete branch '{b.Name}'?\nUnmerged commits may be lost.", "Force delete", true)) await DoAsync(r => r.DeleteBranchAsync(b.Name, true)); }),
            null,
            ("Copy name", () => Clipboard.SetText(b.Name)));
    }

    private TreeViewItem Group(string key, string title, object? tag)
        => Node(key, new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("MutedBrush") }, tag);

    private TreeViewItem Node(string key, object header, object? tag)
    {
        var it = new TreeViewItem { Header = header, Tag = tag, IsExpanded = !_collapsed.Contains(key) };
        it.RequestBringIntoView += RefTree_RequestBringIntoView; // must sit on the item: the tree's ScrollViewer handles the event before it reaches the TreeView
        it.Expanded += (s, e) => { if (ReferenceEquals(e.OriginalSource, it)) _collapsed.Remove(key); };
        it.Collapsed += (s, e) => { if (ReferenceEquals(e.OriginalSource, it)) _collapsed.Add(key); };
        return it;
    }

    private static TextBlock Header(string name, bool current, string? suffix)
    {
        var tb = new TextBlock();
        if (current) tb.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0xD1, 0x85)) });
        tb.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = current ? FontWeights.Bold : FontWeights.Normal });
        if (suffix != null) tb.Inlines.Add(new System.Windows.Documents.Run("  " + suffix) { Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)), FontSize = 11 });
        return tb;
    }

    private static ContextMenu Menu(params (string Header, Action Action)?[] items)
    {
        var m = new ContextMenu();
        foreach (var item in items)
        {
            if (item == null) { m.Items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = item.Value.Header };
            var act = item.Value.Action;
            mi.Click += (_, _) => act();
            m.Items.Add(mi);
        }
        return m;
    }

    private static bool Reselect(ItemCollection items, object tag)
    {
        foreach (TreeViewItem it in items)
        {
            if (it.Tag != null && SameRef(it.Tag, tag)) { it.IsSelected = true; return true; }
            if (Reselect(it.Items, tag)) return true;
        }
        return false;
    }

    private static bool SameRef(object a, object b) => (a, b) switch
    {
        (BranchInfo x, BranchInfo y) => x.FullRef == y.FullRef,
        (TagInfo x, TagInfo y) => x.Name == y.Name,
        (StashInfo x, StashInfo y) => x.Id == y.Id,
        _ => false,
    };

    /// <summary>
    /// WPF brings a selected tree item fully into view, including its expanded children and its full width,
    /// which scrolls the panel sideways when a branch name is wider than the panel. Scroll vertically only, and only for the header row.
    /// </summary>
    private void RefTree_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        e.Handled = true;
        if (e.OriginalSource is not TreeViewItem item || !item.IsVisible) return;
        var sv = FindVisualChild<ScrollViewer>(RefTree);
        if (sv == null) return;
        var header = item.Template?.FindName("Bd", item) as FrameworkElement ?? item;
        try
        {
            var top = header.TransformToAncestor(sv).Transform(new Point(0, 0)).Y;
            var bottom = top + header.ActualHeight;
            if (top < 0) sv.ScrollToVerticalOffset(sv.VerticalOffset + top);
            else if (bottom > sv.ViewportHeight) sv.ScrollToVerticalOffset(sv.VerticalOffset + bottom - sv.ViewportHeight);
        }
        catch (InvalidOperationException) { /* not connected to the visual tree yet */ }
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindVisualChild<T>(c) is T found) return found;
        }
        return null;
    }

    private static TreeViewItem? ItemAt(DependencyObject? d)
    {
        while (d != null && d is not TreeViewItem)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }

    private void RefTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(e.OriginalSource as DependencyObject);
        if (item == null) return;
        // right-clicking outside the multi-selection drops it; inside keeps it so "Drop N selected" is offered
        if (_multiStash.Count > 0 && !(item.Tag is StashInfo s && _multiStash.Contains(s.Id))) { _multiStash.Clear(); UpdateStashHighlight(); }
        item.IsSelected = true; item.Focus();
    }

    /// <summary>Ctrl+click toggles a stash in the multi-selection, Shift+click selects a range; a plain click clears it.</summary>
    private void RefTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(e.OriginalSource as DependencyObject);
        var mods = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift);
        if (item?.Tag is StashInfo s && mods != ModifierKeys.None)
        {
            // seed with the tree's own selected stash so "click one, Ctrl+click another" selects both
            if (_multiStash.Count == 0 && (RefTree.SelectedItem as TreeViewItem)?.Tag is StashInfo cur && cur.Id != s.Id) { _multiStash.Add(cur.Id); _stashAnchor = cur.Id; }
            if (mods == ModifierKeys.Shift && _stashAnchor != null)
            {
                int a = _stashes.FindIndex(x => x.Id == _stashAnchor), b = _stashes.FindIndex(x => x.Id == s.Id);
                if (a >= 0 && b >= 0) foreach (var x in _stashes.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1)) _multiStash.Add(x.Id);
            }
            else
            {
                if (!_multiStash.Remove(s.Id)) _multiStash.Add(s.Id);
                _stashAnchor = s.Id;
            }
            UpdateStashHighlight();
            e.Handled = true; // leave the tree's single selection alone
            return;
        }
        if (_multiStash.Count > 0 && mods == ModifierKeys.None) { _multiStash.Clear(); UpdateStashHighlight(); }
        if (item?.Tag is StashInfo plain) _stashAnchor = plain.Id;
    }

    private void UpdateStashHighlight()
    {
        var on = (Brush)FindResource("SelectionBrush");
        foreach (var n in _stashNodes)
            n.Background = n.Tag is StashInfo s && _multiStash.Contains(s.Id) ? on : Brushes.Transparent;
    }

    /// <summary>The multi-selected stashes, or the tree's single selected stash when there is no multi-selection.</summary>
    private List<StashInfo> SelectedStashes()
    {
        var list = _stashes.Where(s => _multiStash.Contains(s.Id)).ToList();
        if (list.Count == 0 && (RefTree.SelectedItem as TreeViewItem)?.Tag is StashInfo s) list.Add(s);
        return list;
    }

    private async Task DropStashesAsync(IReadOnlyList<StashInfo> list)
    {
        if (list.Count == 0) return;
        if (list.Count == 1)
        {
            var one = list[0];
            if (await ConfirmAsync("Drop stash", $"Drop {one.Id}?\n{one.Message}", "Drop", true)) await DoAsync(r => r.StashDropAsync(one.Id));
            return;
        }
        var preview = string.Join("\n", list.Take(12).Select(s => $"{s.Id}: {s.Message}"));
        if (list.Count > 12) preview += $"\n… and {list.Count - 12} more";
        if (!await ConfirmAsync("Drop stashes", $"Drop {list.Count} stashes? This cannot be undone.\n\n{preview}", $"Drop {list.Count}", true)) return;
        // highest index first, so the remaining stash@{n} names stay valid while dropping
        var ordered = list.OrderByDescending(s => StashIndex(s.Id)).ToList();
        await DoAsync(async r =>
        {
            CommandResult? last = null;
            foreach (var s in ordered)
            {
                last = await r.StashDropAsync(s.Id);
                if (last.ExitCode != 0 || last.Cancelled) break;
            }
            return last!;
        });
        _multiStash.Clear();
    }

    private static int StashIndex(string id)
    {
        var m = System.Text.RegularExpressions.Regex.Match(id, @"\{(\d+)\}");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    private async void RefTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RefTree.SelectedItem is not TreeViewItem item) return;
        e.Handled = true;
        switch (item.Tag)
        {
            case BranchInfo { IsRemote: false, IsCurrent: false } b: await DoAsync(r => r.CheckoutAsync(b.Name)); break;
            case BranchInfo { IsRemote: true } rb: await DoAsync(r => r.CheckoutRemoteAsync(rb.Name)); break;
            case TagInfo t: if (await ConfirmAsync("Checkout tag", $"Checkout tag '{t.Name}'? HEAD will be detached.", "Checkout")) await DoAsync(r => r.CheckoutAsync(t.Name)); break;
            case StashInfo s: if (await ConfirmAsync("Apply stash", $"Apply {s.Id}?\n{s.Message}", "Apply")) await DoAsync(r => r.StashApplyAsync(s.Id)); break;
        }
    }

    private async void RefTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_repo == null || (RefTree.SelectedItem as TreeViewItem)?.Tag is not StashInfo s) return;
        await ShowStashDiffAsync(s);
    }

    private async Task ShowStashDiffAsync(StashInfo s)
    {
        if (_repo == null) return;
        _diffList = null;
        _reloadDiff = () => ShowStashDiffAsync(s);
        int serial = ++_diffSerial;
        var patch = await _repo.StashPatchAsync(s.Id);
        if (serial == _diffSerial) Diff.Show($"{s.Id}: {s.Message}", patch, DiffMode.ReadOnly);
    }

    // ------------------------------------------------------------------ log

    private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (LogList.SelectedItem is not CommitInfo c) return;
        if (c.IsWorkingTree) { ShowWorkPanel(); _ = ShowWorkDiffAsync(); }
        else _ = LoadCommitAsync(c);
    }

    private void ShowWorkPanel()
    {
        WorkPanel.Visibility = Visibility.Visible;
        CommitPanel.Visibility = Visibility.Collapsed;
    }

    private async Task LoadCommitAsync(CommitInfo c)
    {
        if (_repo == null) return;
        int serial = ++_commitSerial;
        WorkPanel.Visibility = Visibility.Collapsed;
        CommitPanel.Visibility = Visibility.Visible;
        CommitHeader.Text = $"Commit {c.ShortHash}";
        CommitMeta.Text = $"{c.Hash}\n{c.Author} <{c.Email}>   {c.DateText}" + (c.Parents.Length > 0 ? $"\nparents: {string.Join(" ", c.Parents.Select(p => p[..Math.Min(8, p.Length)]))}" : "");
        CommitBody.Text = c.Subject;
        CommitFiles.ItemsSource = null;
        Diff.Clear();
        var body = await _repo.CommitBodyAsync(c.Hash);
        if (serial != _commitSerial) return;
        CommitBody.Text = body;
        var files = await _repo.CommitFilesAsync(c);
        if (serial != _commitSerial) return;
        var entries = SortEntries(files.Select(f => new ChangeEntry(f, true)));
        CommitFiles.ItemsSource = entries;
        if (entries.Count > 0) CommitFiles.SelectedIndex = 0;
    }

    private void CommitFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommitFiles.SelectedItem is ChangeEntry entry && LogList.SelectedItem is CommitInfo { IsWorkingTree: false } c)
            _ = ShowCommitDiffAsync(c, entry);
    }

    private async Task ShowCommitDiffAsync(CommitInfo c, ChangeEntry entry)
    {
        if (_repo == null) return;
        _reloadDiff = () => ShowCommitDiffAsync(c, entry);
        _diffList = CommitFiles;
        int serial = ++_diffSerial;
        var text = await _repo.DiffCommitFileAsync(c, entry.File);
        if (serial != _diffSerial) return;
        Diff.Show($"{entry.Display}  @ {c.ShortHash}", text, DiffMode.ReadOnly, history: CommitContext(c, entry.Path, entry.File.OldPath));
    }

    // ------------------------------------------------------------------ file history / blame

    private string? _histPath, _histRev;
    private CancellationTokenSource? _histCts;
    /// <summary>Ticks the elapsed time in the History header while a query runs, so slow ones visibly make progress.</summary>
    private DispatcherTimer? _histTimer;
    private string _histBusyText = "";
    private DateTime _histStarted;

    private static DiffHistoryContext CommitContext(CommitInfo c, string path, string? prevPath)
        => new(path, prevPath, c.Hash, c.Parents.Length > 0 ? c.Parents[0] : null);

    /// <summary>Starts a new History request: cancels the previous one's git queries and returns the new request's token.</summary>
    private CancellationToken BeginHistory(string path, string? rev)
    {
        _histCts?.Cancel();
        _histCts = new CancellationTokenSource();
        _histPath = path; _histRev = rev;
        BtnHistCommits.IsEnabled = BtnHistBlame.IsEnabled = true;
        _histTimer?.Stop();
        _histStarted = DateTime.Now;
        ShowPanel("history");
        return _histCts.Token;
    }

    private void HistoryBusy(string text)
    {
        _histBusyText = text;
        if (_histTimer == null)
        {
            _histTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _histTimer.Tick += (_, _) => UpdateHistoryBusy();
        }
        _histTimer.Start();
        UpdateHistoryBusy();
    }

    private void UpdateHistoryBusy()
    {
        var spin = "◐◓◑◒"[(int)((DateTime.Now - _histStarted).TotalMilliseconds / 250) % 4];
        HistoryHeader.Text = $"{spin} {_histBusyText}  {(DateTime.Now - _histStarted).TotalSeconds:0}s";
    }

    private void HistoryDone(string text)
    {
        _histTimer?.Stop();
        HistoryHeader.Text = text;
    }

    private static string Commits(int n, bool more = false) => $"{n}{(more ? "+" : "")} commit{(n == 1 ? "" : "s")}";

    /// <summary>
    /// Commits that changed <paramref name="path"/>, up to <paramref name="rev"/> (null = HEAD; blame then means the working tree).
    /// The plain history shows straight away; following renames back past them can take much longer in a big repo, so it
    /// runs afterwards and adds the older commits when it finishes.
    /// </summary>
    private async Task ShowFileHistoryAsync(string path, string? rev)
    {
        if (_repo == null) return;
        var ct = BeginHistory(path, rev);
        var what = $"History of {path}{RevSuffix(rev)}";
        BlameList.Visibility = Visibility.Collapsed; HistoryList.Visibility = Visibility.Visible;
        HistoryList.ItemsSource = null;
        HistoryBusy($"{what} — loading…");
        var quick = await _repo.FileHistoryAsync(path, rev, follow: false, ct);
        if (ct.IsCancellationRequested) return;
        HistoryList.ItemsSource = quick;
        HistoryBusy($"{what} — {Commits(quick.Count)} so far · following renames for older commits…");

        var full = await _repo.FileHistoryAsync(path, rev, follow: true, ct);
        if (ct.IsCancellationRequested) return;
        if (full.Count > quick.Count)
        {
            var sel = (HistoryList.SelectedItem as HistoryEntry)?.Commit.Hash;
            _syncingSelection = true;
            HistoryList.ItemsSource = full;
            HistoryList.SelectedItem = full.FirstOrDefault(h => h.Commit.Hash == sel);
            _syncingSelection = false;
        }
        var shown = Math.Max(full.Count, quick.Count);
        HistoryDone(shown == 0 ? $"No commits change {path}{RevSuffix(rev)} yet" : $"{what} — {Commits(shown, shown >= 1000)}");
    }

    /// <summary>Commits that changed lines <paramref name="start"/>–<paramref name="end"/> of the file as it is in <paramref name="rev"/>.</summary>
    private async Task ShowLineHistoryAsync(string rev, string path, int start, int end)
    {
        if (_repo == null) return;
        var ct = BeginHistory(path, rev);
        var what = $"History of {(start == end ? $"line {start}" : $"lines {start}–{end}")} of {path}{RevSuffix(rev)}";
        BlameList.Visibility = Visibility.Collapsed; HistoryList.Visibility = Visibility.Visible;
        HistoryList.ItemsSource = null;
        HistoryBusy($"{what} — searching (this can take a while in a big repo)…");
        var list = await _repo.LineHistoryAsync(rev, path, start, end, ct);
        if (ct.IsCancellationRequested) return;
        HistoryList.ItemsSource = list;
        HistoryDone(list.Count == 0 ? $"No history found for {what[11..]}" : $"{what} — {Commits(list.Count)}");
        if (list.Count > 0) HistoryList.SelectedIndex = 0;
    }

    /// <summary>Blame of <paramref name="path"/> at <paramref name="rev"/> (null = working tree), scrolled to <paramref name="line"/>.</summary>
    private async Task ShowBlameAsync(string path, string? rev, int line)
    {
        if (_repo == null) return;
        var ct = BeginHistory(path, rev);
        var what = $"Blame {path}{(rev == null ? " (working tree)" : RevSuffix(rev))}";
        HistoryList.Visibility = Visibility.Collapsed; BlameList.Visibility = Visibility.Visible;
        BlameList.ItemsSource = null;
        HistoryBusy($"{what} — loading…");
        var (lines, error) = await _repo.BlameAsync(path, rev, ct);
        if (ct.IsCancellationRequested) return;
        BlameList.ItemsSource = lines;
        HistoryDone(error != null ? $"Blame failed: {error}" : $"{what} — {lines.Count} lines, {Commits(lines.Select(l => l.Hash).Distinct().Count())}");
        if (lines.Count == 0) return;
        var target = lines[Math.Clamp(line - 1, 0, lines.Count - 1)];
        _syncingSelection = true;
        BlameList.SelectedItem = target;
        _syncingSelection = false;
        // Scroll a few lines past the target first so it lands with some context above it.
        BlameList.ScrollIntoView(lines[Math.Min(lines.Count - 1, target.LineNo + 8)]);
        BlameList.ScrollIntoView(target);
    }

    private string RevSuffix(string? rev) => rev == null ? "" : $" @ {(rev.Length >= 40 ? rev[..8] : rev)}";

    private async void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _repo == null || HistoryList.SelectedItem is not HistoryEntry h) return;
        await ShowHistoryDiffAsync(h);
    }

    private async Task ShowHistoryDiffAsync(HistoryEntry h)
    {
        if (_repo == null) return;
        _reloadDiff = () => ShowHistoryDiffAsync(h);
        _diffList = null;
        int serial = ++_diffSerial;
        var ctx = CommitContext(h.Commit, h.Path, h.PrevPath);
        if (h.LinePatch != null)
        {
            Diff.Show($"{h.Path}  @ {h.Commit.ShortHash}  (followed lines only)", h.LinePatch, DiffMode.ReadOnly, history: ctx);
            return;
        }
        var text = await _repo.DiffCommitFileAsync(h.Commit, new FileStatus { Path = h.Path, OldPath = h.PrevPath });
        if (serial == _diffSerial) Diff.Show($"{h.PathText}  @ {h.Commit.ShortHash}", text, DiffMode.ReadOnly, history: ctx);
    }

    private async void BlameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _repo == null || BlameList.SelectedItem is not BlameLine b) return;
        await ShowBlameDiffAsync(b);
    }

    private async Task ShowBlameDiffAsync(BlameLine b)
    {
        if (_repo == null) return;
        _reloadDiff = () => ShowBlameDiffAsync(b);
        _diffList = null;
        int serial = ++_diffSerial;
        if (b.Uncommitted) { Diff.ShowMessage("Not committed yet", $"Line {b.LineNo} has uncommitted changes; the Changes panel shows them."); return; }
        var c = new CommitInfo { Hash = b.Hash, Parents = b.PrevHash != null ? new[] { b.PrevHash } : Array.Empty<string>() };
        var prev = b.PrevFileName != b.FileName ? b.PrevFileName : null;
        var text = await _repo.DiffCommitFileAsync(c, new FileStatus { Path = b.FileName, OldPath = prev });
        if (serial == _diffSerial)
            Diff.Show($"{b.FileName}  @ {b.Hash[..8]}  {b.Author}: {b.Summary}", text, DiffMode.ReadOnly, history: CommitContext(c, b.FileName, prev));
    }

    private void HistCommits_Click(object s, RoutedEventArgs e) { if (_histPath != null) _ = ShowFileHistoryAsync(_histPath, _histRev); }
    private void HistBlame_Click(object s, RoutedEventArgs e) { if (_histPath != null) _ = ShowBlameAsync(_histPath, _histRev, (BlameList.SelectedItem as BlameLine)?.LineNo ?? 1); }

    /// <summary>Follows one blamed line back from the commit that last changed it, using its line number in that commit.</summary>
    private void BlameLineHistory_Click(object s, RoutedEventArgs e)
    {
        if (BlameList.SelectedItem is not BlameLine b) return;
        if (b.Uncommitted) { _ = Dialogs.InfoAsync("Not committed yet", "This line has uncommitted changes, so it has no history yet."); return; }
        _ = ShowLineHistoryAsync(b.Hash, b.FileName, b.OrigLineNo, b.OrigLineNo);
    }

    private string? HistorySelectedHash => HistoryList.IsVisible ? (HistoryList.SelectedItem as HistoryEntry)?.Commit.Hash
                                         : BlameList.SelectedItem is BlameLine { Uncommitted: false } b ? b.Hash : null;

    private void HistCopyHash_Click(object s, RoutedEventArgs e) { if (HistorySelectedHash is { } h) Clipboard.SetText(h); }

    private void HistShowInLog_Click(object s, RoutedEventArgs e)
    {
        if (HistorySelectedHash is not { } hash) return;
        var c = (LogList.ItemsSource as IEnumerable<CommitInfo>)?.FirstOrDefault(x => x.Hash == hash);
        if (c == null)
        {
            ConsoleView.Append(OutputKind.Info, $"{hash[..8]} isn't in the loaded log. It may be further back (Load more) or on a branch the log isn't showing.");
            ShowPanel("console");
            return;
        }
        ShowPanel("log");
        LogList.SelectedItem = c;
        LogList.ScrollIntoView(c);
    }

    /// <summary>File list menu: from a commit's file list the history starts at that commit, otherwise at HEAD.</summary>
    private (ChangeEntry Entry, string? Rev)? MenuFile(object s)
    {
        if (MenuEntry(s) is not { } entry) return null;
        var fromCommit = (s as MenuItem)?.Parent is ContextMenu { PlacementTarget: var t } && t == CommitFiles;
        return (entry, fromCommit ? ContextCommit?.Hash : null);
    }

    private void FileHistory_Click(object s, RoutedEventArgs e)
    {
        if (MenuFile(s) is not { } f) return;
        var (entry, rev) = f;
        if (entry.File.Untracked) { _ = Dialogs.InfoAsync("No history", $"{entry.Path} is untracked, so it has no commits yet."); return; }
        _ = ShowFileHistoryAsync(entry.Path, rev);
    }

    private void FileBlame_Click(object s, RoutedEventArgs e)
    {
        if (MenuFile(s) is not { } f) return;
        var (entry, rev) = f;
        if (entry.File.Untracked) { _ = Dialogs.InfoAsync("No history", $"{entry.Path} is untracked, so it has no commits yet."); return; }
        if (entry.File.X == 'D' || (entry.File.Y == 'D' && rev == null)) { _ = Dialogs.InfoAsync("Deleted file", $"{entry.Path} is deleted, so there are no lines to blame. Use History instead."); return; }
        _ = ShowBlameAsync(entry.Path, rev, 1);
    }

    private CommitInfo? ContextCommit => LogList.SelectedItem is CommitInfo { IsWorkingTree: false } c ? c : null;

    private void LogList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ContextCommit == null) e.Handled = true;
    }

    private void LogCheckout_Click(object s, RoutedEventArgs e) { if (ContextCommit is { } c) _ = DoAsync(r => r.CheckoutAsync(c.Hash)); }
    private void LogNewBranch_Click(object s, RoutedEventArgs e) { if (ContextCommit is { } c) CreateBranchAt(c.Hash); }
    private void LogNewTag_Click(object s, RoutedEventArgs e) { if (ContextCommit is { } c) CreateTagAt(c.Hash); }
    private void LogCherryPick_Click(object s, RoutedEventArgs e) { if (ContextCommit is { } c) _ = DoAsync(r => r.CherryPickAsync(c.Hash)); }
    private void LogRevert_Click(object s, RoutedEventArgs e) { if (ContextCommit is { } c) _ = DoAsync(r => r.RevertAsync(c.Hash)); }
    private void LogCopyHash_Click(object s, RoutedEventArgs e) { if (ContextCommit is { } c) Clipboard.SetText(c.Hash); }
    private void LogCopyMessage_Click(object s, RoutedEventArgs e) { if (ContextCommit is { } c) Clipboard.SetText(c.Subject); }
    private async void LogReset_Click(object s, RoutedEventArgs e)
    {
        if (ContextCommit is not { } c || s is not MenuItem mi) return;
        var mode = (string)mi.Tag;
        if (mode == "hard" && !await ConfirmAsync("Reset --hard", $"Hard reset '{_head.Branch}' to {c.ShortHash}?\n\nAll uncommitted changes will be lost.", "Reset", true)) return;
        await DoAsync(r => r.ResetAsync(mode, c.Hash));
    }

    private void LogFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyLogFilter();
    private void LogScope_Click(object sender, RoutedEventArgs e)
    {
        _settings.LogScope = (string)((MenuItem)sender).Tag;
        _settings.Save();
        ApplyLogScope();
        _ = RefreshAsync();
    }

    private void ApplyLogScope()
    {
        BtnLogScope.Content = _settings.LogScope switch { "head" => "Current branch \u25BE", "all" => "All branches \u25BE", _ => "Local branches \u25BE" };
        if (FindResource("LogScopeMenu") is ContextMenu menu)
            foreach (var item in menu.Items.OfType<MenuItem>())
                item.IsChecked = (string)item.Tag == _settings.LogScope;
    }
    private void LoadMore_Click(object sender, RoutedEventArgs e) { _logLimit += 3000; _ = RefreshAsync(); }

    // ------------------------------------------------------------------ working tree

    private ListBox? _activeFileList;

    private void UnstagedList_SelectionChanged(object sender, SelectionChangedEventArgs e) => FileListSelected(UnstagedList, StagedList);
    private void StagedList_SelectionChanged(object sender, SelectionChangedEventArgs e) => FileListSelected(StagedList, UnstagedList);

    private void FileListSelected(ListBox active, ListBox other)
    {
        if (_syncingSelection) return;
        if (active.SelectedItems.Count == 0) return;
        _syncingSelection = true;
        other.SelectedItems.Clear();
        _syncingSelection = false;
        _activeFileList = active;
        _ = ShowWorkDiffAsync();
    }

    private async Task ShowWorkDiffAsync()
    {
        if (_repo == null) return;
        _reloadDiff = ShowWorkDiffAsync;
        var entry = (_activeFileList ?? UnstagedList).SelectedItems.OfType<ChangeEntry>().FirstOrDefault()
                    ?? StagedList.SelectedItems.OfType<ChangeEntry>().FirstOrDefault()
                    ?? UnstagedList.SelectedItems.OfType<ChangeEntry>().FirstOrDefault();
        int serial = ++_diffSerial;
        if (entry == null) { Diff.Clear(); return; }
        _diffList = entry.Staged ? StagedList : UnstagedList;
        var (text, isLfs) = await _repo.DiffAsync(entry.File, entry.Staged);
        if (serial != _diffSerial) return;
        // LFS content diffs can't be applied to the index (it holds pointers), so no line/hunk staging.
        var mode = isLfs ? DiffMode.ReadOnly
                 : entry.Staged ? DiffMode.Staged
                 : entry.File.Untracked || entry.File.Conflicted ? DiffMode.ReadOnly
                 : DiffMode.Unstaged;
        // The working-tree side can be edited in place, except for LFS (diffed through textconv) and conflicts (combined diff).
        var workFile = entry.Staged || isLfs || entry.File.Conflicted ? null
                     : System.IO.Path.Combine(_repo.Root, entry.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        // Lines here trace back from HEAD (the left side, near enough when staged and unstaged changes mix); blame uses the working tree.
        var history = entry.File.Untracked ? null : new DiffHistoryContext(entry.Path, entry.File.OldPath, null, "HEAD");
        Diff.Show((entry.Staged ? "Staged: " : "Unstaged: ") + entry.Display, text, mode, workFile, history);
    }

    /// <summary>
    /// Change navigation ran off the end of the diff: select the next/previous file. The working tree steps through
    /// the unstaged list and on into the staged one.
    /// </summary>
    private bool StepDiffFile(int dir)
    {
        if (_diffList == null) return false;
        var lists = _diffList == CommitFiles ? new ListBox[] { CommitFiles } : new ListBox[] { UnstagedList, StagedList };
        var all = lists.SelectMany(l => l.Items.OfType<ChangeEntry>().Select(e => (List: l, Entry: e))).ToList();
        int cur = all.FindIndex(x => x.List == _diffList && x.List.SelectedItems.Contains(x.Entry));
        int next = cur + dir;
        if (cur < 0 || next < 0 || next >= all.Count) return false;
        var (list, entry) = all[next];
        // The other list is cleared by FileListSelected; clearing this one first makes the new file the only selection.
        _syncingSelection = true;
        list.SelectedItems.Clear();
        _syncingSelection = false;
        list.SelectedItem = entry;
        list.ScrollIntoView(entry);
        return true;
    }

    private List<ChangeEntry> SelectedEntries(ListBox list) => list.SelectedItems.OfType<ChangeEntry>().ToList();

    private void Stage_Click(object s, RoutedEventArgs e) => StageSelected();
    private void UnstagedList_MouseDoubleClick(object s, MouseButtonEventArgs e) { if (OnFileRow(UnstagedList, e)) StageSelected(); }
    /// <summary>True when a double-click landed on a file row rather than a column header or empty space.</summary>
    private static bool OnFileRow(ListView list, MouseButtonEventArgs e)
        => e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(list, d) is ListViewItem;
    private void StageSelected()
    {
        var sel = SelectedEntries(UnstagedList);
        if (sel.Count > 0) _ = DoAsync(r => r.StageAsync(sel.Select(x => x.Path)));
    }

    private void Unstage_Click(object s, RoutedEventArgs e) => UnstageSelected();
    private void StagedList_MouseDoubleClick(object s, MouseButtonEventArgs e) { if (OnFileRow(StagedList, e)) UnstageSelected(); }
    private void UnstageSelected()
    {
        var sel = SelectedEntries(StagedList);
        if (sel.Count > 0) _ = DoAsync(r => r.UnstageAsync(sel.Select(x => x.Path)));
    }

    private void StageAll_Click(object s, RoutedEventArgs e) => _ = DoAsync(r => r.StageAllAsync());
    private void UnstageAll_Click(object s, RoutedEventArgs e) => _ = DoAsync(r => r.UnstageAllAsync());

    private async void Discard_Click(object s, RoutedEventArgs e)
    {
        var sel = SelectedEntries(UnstagedList);
        if (sel.Count == 0) return;
        var untracked = sel.Where(x => x.File.Untracked).Select(x => x.Path).ToList();
        var tracked = sel.Where(x => !x.File.Untracked).Select(x => x.Path).ToList();
        var what = new List<string>();
        if (tracked.Count > 0) what.Add($"discard changes in {tracked.Count} tracked file(s)");
        if (untracked.Count > 0) what.Add($"delete {untracked.Count} untracked file(s)/folder(s)");
        var list = string.Join("\n", sel.Take(12).Select(x => "  " + x.Display)) + (sel.Count > 12 ? "\n  …" : "");
        if (!await ConfirmAsync("Discard changes", $"This will {string.Join(" and ", what)}:\n\n{list}\n\nThis cannot be undone.", "Discard", true)) return;
        await DoAsync(async r =>
        {
            CommandResult res = new(0, "", "", false);
            if (tracked.Count > 0) res = await r.DiscardAsync(tracked);
            if (untracked.Count > 0) res = await r.CleanAsync(untracked);
            return res;
        });
    }

    private void UnstagedList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        bool conflict = SelectedEntries(UnstagedList).Any(x => x.File.Conflicted);
        var v = conflict ? Visibility.Visible : Visibility.Collapsed;
        MenuResolveOurs.Visibility = v; MenuResolveTheirs.Visibility = v; MenuResolveSep.Visibility = v;
    }

    private void ResolveOurs_Click(object s, RoutedEventArgs e) => Resolve("ours");
    private void ResolveTheirs_Click(object s, RoutedEventArgs e) => Resolve("theirs");
    private void Resolve(string side)
    {
        var sel = SelectedEntries(UnstagedList).Where(x => x.File.Conflicted).Select(x => x.Path).ToList();
        if (sel.Count == 0) return;
        _ = DoAsync(async r =>
        {
            CommandResult res = new(0, "", "", false);
            foreach (var p in sel) { res = await r.ResolveAsync(p, side); if (!res.Ok) return res; }
            return await r.StageAsync(sel);
        });
    }

    private ChangeEntry? MenuEntry(object sender)
    {
        var list = (sender as MenuItem)?.Parent is ContextMenu cm ? cm.PlacementTarget as ListBox : null;
        return list?.SelectedItem as ChangeEntry;
    }

    private void OpenFile_Click(object s, RoutedEventArgs e)
    {
        if (_repo == null || MenuEntry(s) is not { } entry) return;
        var full = Path.Combine(_repo.Root, entry.Path);
        try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); }
        catch (Exception ex) { ConsoleView.Append(OutputKind.Error, ex.Message); }
    }

    private async void OpenInSolution_Click(object s, RoutedEventArgs e)
    {
        if (_repo == null || MenuEntry(s) is not { } entry) return;
        var full = Path.Combine(_repo.Root, entry.Path.Replace('/', '\\'));
        ConsoleView.Append(OutputKind.Info, $"Opening {entry.Path} in Visual Studio…");
        try { ConsoleView.Append(OutputKind.Info, await VisualStudio.OpenInSolutionAsync(full, _repo.Root)); }
        catch (Exception ex) { ConsoleView.Append(OutputKind.Error, ex.Message); }
    }

    private void ShowInExplorer_Click(object s, RoutedEventArgs e)
    {
        if (_repo == null || MenuEntry(s) is not { } entry) return;
        var full = Path.Combine(_repo.Root, entry.Path.Replace('/', '\\'));
        try { Process.Start("explorer.exe", File.Exists(full) ? $"/select,\"{full}\"" : $"\"{Path.GetDirectoryName(full)}\""); }
        catch (Exception ex) { ConsoleView.Append(OutputKind.Error, ex.Message); }
    }

    private void CopyPath_Click(object s, RoutedEventArgs e)
    {
        if (MenuEntry(s) is { } entry) Clipboard.SetText(entry.Path);
    }

    private void Ignore_Click(object s, RoutedEventArgs e)
    {
        if (_repo == null) return;
        var sel = SelectedEntries(UnstagedList).Select(x => x.Path).ToList();
        if (sel.Count == 0) return;
        var ignore = Path.Combine(_repo.Root, ".gitignore");
        try
        {
            var existing = File.Exists(ignore) ? File.ReadAllText(ignore) : "";
            var add = string.Join("\n", sel.Select(p => "/" + p));
            File.WriteAllText(ignore, (existing.Length > 0 && !existing.EndsWith('\n') ? existing + "\n" : existing) + add + "\n");
            ConsoleView.Append(OutputKind.Info, $"Added {sel.Count} pattern(s) to .gitignore");
        }
        catch (Exception ex) { ConsoleView.Append(OutputKind.Error, ex.Message); }
        _ = RefreshAsync();
    }

    private async Task ApplyHunkAsync(HunkOp op, string patch)
    {
        await DoAsync(r => op switch
        {
            HunkOp.Stage => r.ApplyPatchAsync(patch, cached: true, reverse: false),
            HunkOp.Unstage => r.ApplyPatchAsync(patch, cached: true, reverse: true),
            _ => r.ApplyPatchAsync(patch, cached: false, reverse: true),
        });
    }

    // ------------------------------------------------------------------ commit

    private async void Amend_Click(object s, RoutedEventArgs e)
    {
        if (_repo == null || AmendCheck.IsChecked != true || CommitMessage.Text.Trim().Length > 0) return;
        CommitMessage.Text = await _repo.CommitBodyAsync("HEAD");
    }

    private void Commit_Click(object s, RoutedEventArgs e) => _ = CommitAsync(false);
    private void CommitPush_Click(object s, RoutedEventArgs e) => _ = CommitAsync(true);

    private async Task CommitAsync(bool push)
    {
        if (_repo == null) return;
        var msg = CommitMessage.Text.Trim();
        bool amend = AmendCheck.IsChecked == true;
        if (msg.Length == 0) { await Dialogs.InfoAsync("Commit", "Enter a commit message first."); CommitMessage.Focus(); return; }
        if (!amend && !_status.Any(x => x.HasStaged))
        {
            if (_status.Count == 0) { await Dialogs.InfoAsync("Commit", "Nothing to commit — the working tree is clean."); return; }
            if (!await ConfirmAsync("Commit", "Nothing is staged. Stage all changes and commit?", "Stage all & commit")) return;
            await _repo.StageAllAsync();
        }
        var res = await _repo.CommitAsync(msg, amend);
        if (res.Ok)
        {
            CommitMessage.Clear();
            AmendCheck.IsChecked = false;
            if (push) await PushAsync(amend ? "--force-with-lease" : "");
        }
        await RefreshAsync();
    }

    // ------------------------------------------------------------------ toolbar actions

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || FindResource((string)btn.Tag) is not ContextMenu menu) return;
        if ((string)btn.Tag == "RepoMenu") PopulateRecent(menu);
        if ((string)btn.Tag == "ViewMenu") PopulateViewMenu(menu);
        menu.PlacementTarget = btn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void PopulateRecent(ContextMenu repoMenu)
    {
        var recent = repoMenu.Items.OfType<MenuItem>().First(m => (string)m.Header == "Recent");
        recent.Items.Clear();
        foreach (var r in _settings.RecentRepos)
        {
            var mi = new MenuItem { Header = r };
            mi.Click += (_, _) => _ = OpenRepoAsync(r);
            recent.Items.Add(mi);
        }
        recent.IsEnabled = recent.Items.Count > 0;
    }

    private void Refresh_Click(object s, RoutedEventArgs e) => _ = RefreshAsync();
    private void Fetch_Click(object s, RoutedEventArgs e) => _ = DoAsync(r => r.FetchAsync());
    /// <summary>Main half of the split Pull button: runs whichever pull type was last chosen from the ▾ menu.</summary>
    private void PullDefault_Click(object s, RoutedEventArgs e) => _ = DoAsync(r => r.PullAsync(_settings.PullMode));

    /// <summary>▾ menu: remembers the chosen pull type as the new default, then pulls with it.</summary>
    private void Pull_Click(object s, RoutedEventArgs e)
    {
        _settings.PullMode = (string)((MenuItem)s).Tag;
        _settings.Save();
        ApplyPullMode();
        _ = DoAsync(r => r.PullAsync(_settings.PullMode));
    }

    private static readonly Dictionary<string, string> FileSortColumns = new() { ["Name"] = "name", ["Ext"] = "ext", ["Path"] = "path" };

    /// <summary>
    /// File-list column header click: sorts every file list by that column, or flips the direction if it's already the sort.
    /// The choice is remembered.
    /// </summary>
    private void FileHeader_Click(object s, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column.Header: string header }) return;
        if (!FileSortColumns.TryGetValue(header.Split(' ')[0], out var mode)) return;
        _settings.FileSortDescending = mode == _settings.FileSort && !_settings.FileSortDescending;
        _settings.FileSort = mode;
        _settings.Save();
        UpdateFileHeaders();
        ApplyStatus();
        if (CommitFiles.ItemsSource is IEnumerable<ChangeEntry> files)
        {
            var sel = CommitFiles.SelectedItem;
            CommitFiles.ItemsSource = SortEntries(files);
            CommitFiles.SelectedItem = sel;
        }
    }

    /// <summary>Puts ▲/▼ on the sorted column of each file list.</summary>
    private void UpdateFileHeaders()
    {
        var arrow = _settings.FileSortDescending ? " ▼" : " ▲";
        foreach (var view in new[] { UnstagedView, StagedView, CommitFilesView })
            foreach (var col in view.Columns)
                if (col.Header is string h && FileSortColumns.TryGetValue(h.Split(' ')[0], out var mode))
                    col.Header = h.Split(' ')[0] + (mode == _settings.FileSort ? arrow : "");
    }

    /// <summary>
    /// Orders a file list by the chosen column and direction. Only the primary key flips when descending;
    /// for extension, "X.prefab.meta" sorts as a .prefab right after X.prefab so Unity meta churn stays out of the way.
    /// </summary>
    private List<ChangeEntry> SortEntries(IEnumerable<ChangeEntry> entries)
    {
        static string Core(string p)
        {
            var n = System.IO.Path.GetFileName(p);
            return n.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? n[..^5] : n;
        }
        var desc = _settings.FileSortDescending;
        IOrderedEnumerable<ChangeEntry> By(Func<ChangeEntry, string> key, StringComparer cmp)
            => desc ? entries.OrderByDescending(key, cmp) : entries.OrderBy(key, cmp);
        var cmp = StringComparer.OrdinalIgnoreCase;
        return _settings.FileSort switch
        {
            "name" => By(e => e.Name, cmp).ThenBy(e => e.Path, cmp).ToList(),
            "ext" => By(e => System.IO.Path.GetExtension(Core(e.Path)), cmp)
                         .ThenBy(e => Core(e.Path), cmp)
                         .ThenBy(e => e.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                         .ThenBy(e => e.Path, cmp).ToList(),
            _ => By(e => e.Path, StringComparer.Ordinal).ToList(),
        };
    }

    private void ApplyPullMode()
    {
        var mode = _settings.PullMode;
        var (label, cmd) = mode switch
        {
            "rebase" => ("Pull --rebase", "git pull --rebase"),
            "ff" => ("Pull --ff-only", "git pull --ff-only"),
            _ => ("Pull (merge)", "git pull"),
        };
        BtnPull.Content = label;
        BtnPull.ToolTip = cmd + " --autostash\nLocal changes are stashed and re-applied automatically\nUse \u25BE to change the pull type";
        if (FindResource("PullMenu") is ContextMenu menu)
            foreach (var item in menu.Items.OfType<MenuItem>())
                item.IsChecked = (string)item.Tag == mode;
    }
    /// <summary>Main half of the split Push button: pushes the way last chosen from the ▾ menu.</summary>
    private void PushDefault_Click(object s, RoutedEventArgs e) => _ = DoAsync(async _ => { await PushAsync(_settings.PushMode); return new CommandResult(0, "", "", false); });

    /// <summary>▾ menu: remembers the chosen push type as the new default, then pushes with it.</summary>
    private void Push_Click(object s, RoutedEventArgs e)
    {
        _settings.PushMode = (string)((MenuItem)s).Tag;
        _settings.Save();
        ApplyPushMode();
        _ = DoAsync(async _ => { await PushAsync(_settings.PushMode); return new CommandResult(0, "", "", false); });
    }

    private void ApplyPushMode()
    {
        var mode = _settings.PushMode;
        BtnPush.Content = mode.Length == 0 ? "Push" : "Push " + mode;
        BtnPush.ToolTip = ("git push " + mode).Trim() + "\nUse \u25BE to change the push type";
        if (FindResource("PushMenu") is ContextMenu menu)
            foreach (var item in menu.Items.OfType<MenuItem>())
                item.IsChecked = (string)item.Tag == mode;
    }

    private async Task PushAsync(string extra)
    {
        if (_repo == null) return;
        var cur = _refs.Current;
        if (extra != "--tags" && cur != null && cur.Upstream == null && !_head.Detached)
        {
            var remotes = await _repo.RemotesAsync();
            if (remotes.Count == 0) { ConsoleView.Append(OutputKind.Error, "No remotes configured. Add one with: git remote add origin <url>"); return; }
            var remote = remotes.Contains("origin") ? "origin" : remotes[0];
            if (!await ConfirmAsync("Push", $"Branch '{cur.Name}' has no upstream.\nPush to {remote}/{cur.Name} and set it as upstream?", "Push")) return;
            await _repo.PushNewBranchAsync(remote, cur.Name);
            return;
        }
        await _repo.PushAsync(extra);
    }

    private async Task PushBranchAsync(BranchInfo b)
    {
        if (_repo == null) return;
        if (b.Upstream != null) { await DoAsync(r => r.PushAsync(b.Name)); return; }
        var remotes = await _repo.RemotesAsync();
        if (remotes.Count == 0) { ConsoleView.Append(OutputKind.Error, "No remotes configured."); return; }
        var remote = remotes.Contains("origin") ? "origin" : remotes[0];
        await DoAsync(r => r.PushNewBranchAsync(remote, b.Name));
    }

    private async Task PushTagAsync(string tag)
    {
        if (_repo == null) return;
        var remotes = await _repo.RemotesAsync();
        if (remotes.Count == 0) { ConsoleView.Append(OutputKind.Error, "No remotes configured."); return; }
        var remote = remotes.Contains("origin") ? "origin" : remotes[0];
        await DoAsync(r => r.PushTagAsync(remote, tag));
    }

    private void NewBranch_Click(object s, RoutedEventArgs e) => CreateBranchAt(null);

    private async void CreateBranchAt(string? at)
    {
        var res = await Dialogs.InputAsync("New branch", new[] { new DialogField("Branch name"), new DialogField("Start point", at ?? "HEAD") }, "Checkout the new branch", true, "Create");
        if (res == null || res.Values[0].Trim().Length == 0) return;
        var name = res.Values[0].Trim();
        var start = res.Values[1].Trim();
        await DoAsync(r => r.CreateBranchAsync(name, start.Length > 0 && start != "HEAD" ? start : null, res.Checked));
    }

    private async void CreateTagAt(string? at)
    {
        var res = await Dialogs.InputAsync("New tag", new[] { new DialogField("Tag name"), new DialogField("Message (optional, makes an annotated tag)", "", true) }, okText: "Create");
        if (res == null || res.Values[0].Trim().Length == 0) return;
        var name = res.Values[0].Trim();
        await DoAsync(r => r.CreateTagAsync(name, at, res.Values[1].Trim()));
    }

    private async void CheckoutPrompt_Click(object s, RoutedEventArgs e)
    {
        var target = await Dialogs.AskAsync("Checkout", "Branch, tag or commit", "", "Checkout");
        if (target is { Length: > 0 }) await DoAsync(r => r.CheckoutAsync(target.Trim()));
    }

    private async void Merge_Click(object s, RoutedEventArgs e)
    {
        var target = await Dialogs.AskAsync("Merge", $"Merge into '{_head.Branch}' from branch", "", "Merge");
        if (target is { Length: > 0 }) await DoAsync(r => r.MergeAsync(target.Trim()));
    }

    private async void Rebase_Click(object s, RoutedEventArgs e)
    {
        var target = await Dialogs.AskAsync("Rebase", $"Rebase '{_head.Branch}' onto", "", "Rebase");
        if (target is { Length: > 0 }) await DoAsync(r => r.RebaseAsync(target.Trim()));
    }

    private async void StashPush_Click(object s, RoutedEventArgs e)
    {
        if (_status.Count == 0) { await Dialogs.InfoAsync("Stash", "Nothing to stash — the working tree is clean."); return; }
        var res = await Dialogs.InputAsync("Stash changes", new[] { new DialogField("Message (optional)") }, "Include untracked files", false, "Stash");
        if (res == null) return;
        await DoAsync(r => r.StashPushAsync(res.Values[0].Trim(), res.Checked));
    }

    private void StashPopLatest_Click(object s, RoutedEventArgs e) { if (_stashes.Count > 0) _ = DoAsync(r => r.StashPopAsync(_stashes[0].Id)); }
    private void StashApplyLatest_Click(object s, RoutedEventArgs e) { if (_stashes.Count > 0) _ = DoAsync(r => r.StashApplyAsync(_stashes[0].Id)); }

    private void OpContinue_Click(object s, RoutedEventArgs e) => _ = DoAsync(r => r.OperationContinueAsync(_op));
    private void OpSkip_Click(object s, RoutedEventArgs e) => _ = DoAsync(r => r.OperationSkipAsync(_op));
    private async void OpAbort_Click(object s, RoutedEventArgs e)
    {
        if (await ConfirmAsync("Abort", $"Abort the {_op.ToString().ToLowerInvariant()} and return to the previous state?", "Abort", true))
            await DoAsync(r => r.OperationAbortAsync(_op));
    }

    private void OpenRepo_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Open repository", InitialDirectory = _repo?.Root ?? _settings.LastRepo };
        if (dlg.ShowDialog(this) == true) _ = OpenRepoAsync(dlg.FolderName);
    }

    private async void Clone_Click(object s, RoutedEventArgs e)
    {
        var parent = _repo != null ? Path.GetDirectoryName(_repo.Root) ?? _repo.Root : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var res = await Dialogs.InputAsync("Clone repository", new[] { new DialogField("Repository URL"), new DialogField("Destination folder (will be created)", parent) }, okText: "Clone");
        if (res == null) return;
        var url = res.Values[0].Trim();
        var dest = res.Values[1].Trim();
        if (url.Length == 0 || dest.Length == 0) return;
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
        {
            var name = url.TrimEnd('/').Split('/').Last();
            if (name.EndsWith(".git")) name = name[..^4];
            dest = Path.Combine(dest, name);
        }
        var dir = Path.GetDirectoryName(dest) ?? dest;
        Directory.CreateDirectory(dir);
        var r = await _runner.GitAsync(dir, $"clone --progress {GitRepo.Quote(url)} {GitRepo.Quote(dest)}");
        if (r.Ok) await OpenRepoAsync(dest);
    }

    private async void Init_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose folder for the new repository" };
        if (dlg.ShowDialog(this) != true) return;
        var res = await _runner.GitAsync(dlg.FolderName, "init");
        if (res.Ok) await OpenRepoAsync(dlg.FolderName);
    }

    private void OpenExplorer_Click(object s, RoutedEventArgs e)
    {
        if (_repo != null) Process.Start("explorer.exe", $"\"{_repo.Root}\"");
    }

    private void OpenTerminal_Click(object s, RoutedEventArgs e)
    {
        var dir = _repo?.Root ?? _runner.ConsoleDirectory;
        try { Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{dir}\"") { UseShellExecute = true }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo("pwsh.exe", "-NoExit") { WorkingDirectory = dir, UseShellExecute = true }); }
            catch { Process.Start(new ProcessStartInfo("powershell.exe", "-NoExit") { WorkingDirectory = dir, UseShellExecute = true }); }
        }
    }

    private async void GitPath_Click(object s, RoutedEventArgs e)
    {
        var p = await Dialogs.AskAsync("Git executable", "Path or name of git.exe", _settings.GitPath, "Save");
        if (p is { Length: > 0 }) { _settings.GitPath = p.Trim(); _runner.GitExe = p.Trim(); _settings.Save(); await RefreshAsync(); }
    }

    private void Exit_Click(object s, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ keys

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Dialogs.IsOpen) return;
        if (e.Key == Key.F5) { _ = RefreshAsync(); e.Handled = true; }
        else if (e.Key == Key.F8 && Keyboard.Modifiers == ModifierKeys.None) { Diff.NextChange(); e.Handled = true; }
        else if (e.Key == Key.F8 && Keyboard.Modifiers == ModifierKeys.Shift) { Diff.PrevChange(); e.Handled = true; }
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) { OpenRepo_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && CommitMessage.IsKeyboardFocusWithin) { _ = CommitAsync(false); e.Handled = true; }
        else if (e.Key == Key.OemTilde && Keyboard.Modifiers == ModifierKeys.Control) { ShowPanel("console"); ConsoleView.FocusInput(); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { LogFilter.Focus(); LogFilter.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Delete && RefTree.IsKeyboardFocusWithin && SelectedStashes() is { Count: > 0 } del) { _ = DropStashesAsync(del); e.Handled = true; }
    }
}
