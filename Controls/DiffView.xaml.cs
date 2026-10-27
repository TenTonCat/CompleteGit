using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CompleteGit.Controls;

public enum DiffMode { None, Unstaged, Staged, ReadOnly }
public enum HunkOp { Stage, Unstage, Discard }
public enum DiffLineKind { Header, Hunk, Add, Del, Context, Meta }

public sealed class DiffLine
{
    public DiffLineKind Kind { get; init; }
    public string OldNo { get; init; } = "";
    public string NewNo { get; init; } = "";
    public string Text { get; init; } = "";
    public string Raw { get; init; } = "";
    public int HunkIndex { get; init; } = -1;
    public override string ToString() => Text;
}

public enum DiffRowKind { Full, Context, Change }

/// <summary>One visual row: either a full-width header/hunk/meta line, or a left/right pair.</summary>
public sealed class DiffRow
{
    public DiffRowKind Kind { get; init; }
    public DiffLine? Full { get; init; }
    public DiffLine? Left { get; init; }
    public DiffLine? Right { get; init; }
    public DiffMode Mode { get; init; }
    public int HunkIndex { get; init; } = -1;

    public bool IsFull => Kind == DiffRowKind.Full;
    public bool IsPair => Kind != DiffRowKind.Full;
    public bool HasChange => Kind == DiffRowKind.Change;
    public bool CanStage => HasChange && Mode == DiffMode.Unstaged;
    public bool CanDiscard => HasChange && Mode == DiffMode.Unstaged;
    public bool CanUnstage => HasChange && Mode == DiffMode.Staged;

    public string FullText => Full?.Text ?? "";
    public Brush FullFg => Full?.Kind switch
    {
        DiffLineKind.Hunk => DiffView.HunkFg,
        DiffLineKind.Header when Full.Text.StartsWith("diff --git") || Full.Text.StartsWith("---") || Full.Text.StartsWith("+++") => DiffView.NormalFg,
        _ => DiffView.MutedFg,
    };
    public Brush? Background => Full?.Kind switch
    {
        DiffLineKind.Hunk => DiffView.HunkBg,
        DiffLineKind.Header => DiffView.HeaderBg,
        _ => null,
    };

    public string LeftNo => Left?.OldNo ?? "";
    public string LeftText => Left?.Text ?? "";
    public Brush LeftFg => Left?.Kind == DiffLineKind.Del ? DiffView.DelFg : DiffView.NormalFg;
    public Brush? LeftBg => Left?.Kind == DiffLineKind.Del ? DiffView.DelBg : (HasChange && Left == null ? DiffView.FillerBg : null);

    public string RightNo => Right?.NewNo ?? "";
    public string RightText => Right?.Text ?? "";
    public Brush RightFg => Right?.Kind == DiffLineKind.Add ? DiffView.AddFg : DiffView.NormalFg;
    public Brush? RightBg => Right?.Kind == DiffLineKind.Add ? DiffView.AddBg : (HasChange && Right == null ? DiffView.FillerBg : null);

    // Unified rendering (one DiffLine per row)
    public DiffLine? Unified => Full ?? Left ?? Right;
    public string UnifiedText => Unified?.Text ?? "";
    public Brush UnifiedFg => Unified?.Kind switch
    {
        DiffLineKind.Add => DiffView.AddFg,
        DiffLineKind.Del => DiffView.DelFg,
        DiffLineKind.Hunk => DiffView.HunkFg,
        DiffLineKind.Meta => DiffView.MutedFg,
        _ => FullFg,
    };
    public Brush? UnifiedBg => Unified?.Kind switch
    {
        DiffLineKind.Add => DiffView.AddBg,
        DiffLineKind.Del => DiffView.DelBg,
        _ => null,
    };

    public IEnumerable<DiffLine> Lines
    {
        get
        {
            if (Full != null) yield return Full;
            if (Left != null) yield return Left;
            if (Right != null && !ReferenceEquals(Right, Left)) yield return Right;
        }
    }

    public override string ToString() => IsFull ? FullText : (Left != null && Right != null ? $"{LeftText} | {RightText}" : LeftText + RightText);
}

public partial class DiffView : UserControl
{
    internal static readonly Brush AddBg = Freeze(Color.FromArgb(0xC0, 0x1F, 0x4A, 0x27));
    internal static readonly Brush DelBg = Freeze(Color.FromArgb(0xC0, 0x5A, 0x22, 0x22));
    internal static readonly Brush FillerBg = Freeze(Color.FromArgb(0x60, 0x3A, 0x3A, 0x3A));
    internal static readonly Brush HunkBg = Freeze(Color.FromRgb(0x1E, 0x2A, 0x3A));
    internal static readonly Brush HeaderBg = Freeze(Color.FromRgb(0x2D, 0x2D, 0x30));
    internal static readonly Brush AddFg = Freeze(Color.FromRgb(0xB5, 0xE8, 0xB0));
    internal static readonly Brush DelFg = Freeze(Color.FromRgb(0xF5, 0xB7, 0xB1));
    internal static readonly Brush HunkFg = Freeze(Color.FromRgb(0x9C, 0xDC, 0xFE));
    internal static readonly Brush MutedFg = Freeze(Color.FromRgb(0x8A, 0x8A, 0x8A));
    internal static readonly Brush NormalFg = Freeze(Color.FromRgb(0xD4, 0xD4, 0xD4));
    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private List<string> _headers = new();
    private List<DiffLine> _lines = new();
    private List<List<DiffLine>> _hunks = new();
    private List<DiffRow> _rows = new();
    private DiffMode _mode = DiffMode.None;
    private string _title = "";
    private bool _sideBySide = true;

    public event Action<HunkOp, string>? HunkRequested;
    public event Action<bool>? SideBySideChanged;

    public bool SideBySide
    {
        get => _sideBySide;
        set { SideBySideCheck.IsChecked = value; }
    }

    public DiffView()
    {
        InitializeComponent();
        Lines.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { Copy(right: true); e.Handled = true; }
        };
        Lines.ContextMenuOpening += (_, _) =>
        {
            var u = _mode == DiffMode.Unstaged ? Visibility.Visible : Visibility.Collapsed;
            var s = _mode == DiffMode.Staged ? Visibility.Visible : Visibility.Collapsed;
            MenuStageLines.Visibility = u; MenuDiscardLines.Visibility = u; MenuStageHunk.Visibility = u; MenuDiscardHunk.Visibility = u;
            MenuUnstageLines.Visibility = s; MenuUnstageHunk.Visibility = s;
        };
        ApplyLayout();
    }

    public void Clear()
    {
        _mode = DiffMode.None;
        _title = "";
        TitleText.Text = "Diff";
        StatsText.Text = "";
        LeftTitle.Text = ""; RightTitle.Text = "";
        SideTitles.Visibility = Visibility.Collapsed;
        Lines.ItemsSource = null;
        _headers = new(); _lines = new(); _hunks = new(); _rows = new();
        UpdateButtons();
    }

    public void ShowMessage(string title, string message)
    {
        Clear();
        TitleText.Text = title;
        Lines.ItemsSource = new List<DiffRow> { new() { Kind = DiffRowKind.Full, Full = new DiffLine { Kind = DiffLineKind.Meta, Text = message } } };
    }

    public void Show(string title, string diff, DiffMode mode)
    {
        bool sameFile = title == _title && Lines.ItemsSource != null;
        double offset = sameFile ? ScrollOffset() : 0;

        _mode = mode;
        _title = title;
        TitleText.Text = title;
        (LeftTitle.Text, RightTitle.Text) = mode switch
        {
            DiffMode.Unstaged => ("Index (staged)   ◀ stage      discard ▶", "Working tree"),
            DiffMode.Staged => ("HEAD", "Index (staged)   unstage ▶"),
            _ => ("Before", "After"),
        };

        SideTitles.Visibility = _sideBySide ? Visibility.Visible : Visibility.Collapsed;
        var (lines, headers, hunks, adds, dels) = Parse(diff);
        _lines = lines; _headers = headers; _hunks = hunks;
        if (lines.Count == 0) lines.Add(new DiffLine { Kind = DiffLineKind.Meta, Text = "(no differences)" });
        _rows = BuildRows(lines, mode);
        Lines.ItemsSource = _rows;
        StatsText.Text = adds + dels > 0 ? $"+{adds}  −{dels}" : "";
        UpdateButtons();

        if (sameFile && offset > 0)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => SetScrollOffset(offset));
        else if (_rows.Count > 0)
            Lines.ScrollIntoView(_rows[0]);
    }

    private void UpdateButtons()
    {
        var u = _mode == DiffMode.Unstaged ? Visibility.Visible : Visibility.Collapsed;
        var s = _mode == DiffMode.Staged ? Visibility.Visible : Visibility.Collapsed;
        BtnStageLines.Visibility = u; BtnDiscardLines.Visibility = u; BtnStageHunk.Visibility = u; BtnDiscardHunk.Visibility = u;
        BtnUnstageLines.Visibility = s; BtnUnstageHunk.Visibility = s;
    }

    private void ApplyLayout()
    {
        Lines.ItemTemplate = (DataTemplate)FindResource(_sideBySide ? "SideBySideTemplate" : "UnifiedTemplate");
        SideTitles.Visibility = _sideBySide && _mode != DiffMode.None ? Visibility.Visible : Visibility.Collapsed;
        ScrollViewer.SetHorizontalScrollBarVisibility(Lines, _sideBySide ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        if (_lines.Count > 0)
        {
            _rows = BuildRows(_lines, _mode);
            Lines.ItemsSource = _rows;
        }
    }

    private void SideBySide_Changed(object sender, RoutedEventArgs e)
    {
        bool v = SideBySideCheck.IsChecked == true;
        if (v == _sideBySide) return;
        _sideBySide = v;
        ApplyLayout();
        SideBySideChanged?.Invoke(v);
    }

    // ---------------------------------------------------------------- parsing

    private static (List<DiffLine>, List<string>, List<List<DiffLine>>, int, int) Parse(string diff)
    {
        var lines = new List<DiffLine>();
        var headers = new List<string>();
        var hunks = new List<List<DiffLine>>();
        int oldNo = 0, newNo = 0, adds = 0, dels = 0, hunkIdx = -1;
        const int cap = 40000;
        foreach (var rawLine in diff.Split('\n'))
        {
            var raw = rawLine.TrimEnd('\r');
            if (lines.Count >= cap) { lines.Add(new DiffLine { Kind = DiffLineKind.Meta, Text = "… diff truncated …" }); break; }
            if (raw.StartsWith("@@"))
            {
                hunkIdx++;
                var m = Regex.Match(raw, @"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
                if (m.Success) { oldNo = int.Parse(m.Groups[1].Value); newNo = int.Parse(m.Groups[2].Value); }
                var h = new DiffLine { Kind = DiffLineKind.Hunk, Text = raw, Raw = raw, HunkIndex = hunkIdx };
                hunks.Add(new List<DiffLine> { h });
                lines.Add(h);
                continue;
            }
            if (hunkIdx < 0)
            {
                if (raw.Length == 0 && lines.Count == 0) continue;
                headers.Add(raw);
                lines.Add(new DiffLine { Kind = DiffLineKind.Header, Text = raw, Raw = raw });
                continue;
            }
            if (raw.Length == 0) continue;
            char c = raw[0];
            var text = Expand(raw.Length > 1 ? raw[1..] : "");
            DiffLine line = c switch
            {
                '+' => new DiffLine { Kind = DiffLineKind.Add, NewNo = (newNo++).ToString(), Text = text, Raw = raw, HunkIndex = hunkIdx },
                '-' => new DiffLine { Kind = DiffLineKind.Del, OldNo = (oldNo++).ToString(), Text = text, Raw = raw, HunkIndex = hunkIdx },
                '\\' => new DiffLine { Kind = DiffLineKind.Meta, Text = raw, Raw = raw, HunkIndex = hunkIdx },
                _ => new DiffLine { Kind = DiffLineKind.Context, OldNo = (oldNo++).ToString(), NewNo = (newNo++).ToString(), Text = text, Raw = raw, HunkIndex = hunkIdx },
            };
            if (c == '+') adds++; else if (c == '-') dels++;
            hunks[hunkIdx].Add(line);
            lines.Add(line);
        }
        return (lines, headers, hunks, adds, dels);
    }

    private static string Expand(string s) => s.Contains('\t') ? s.Replace("\t", "    ") : s;

    private List<DiffRow> BuildRows(List<DiffLine> lines, DiffMode mode)
    {
        var rows = new List<DiffRow>(lines.Count);
        if (!_sideBySide)
        {
            foreach (var l in lines)
            {
                rows.Add(l.Kind switch
                {
                    DiffLineKind.Add => new DiffRow { Kind = DiffRowKind.Change, Right = l, Mode = mode, HunkIndex = l.HunkIndex },
                    DiffLineKind.Del => new DiffRow { Kind = DiffRowKind.Change, Left = l, Mode = mode, HunkIndex = l.HunkIndex },
                    DiffLineKind.Context => new DiffRow { Kind = DiffRowKind.Context, Left = l, Right = l, Mode = mode, HunkIndex = l.HunkIndex },
                    _ => new DiffRow { Kind = DiffRowKind.Full, Full = l, Mode = mode, HunkIndex = l.HunkIndex },
                });
            }
            return rows;
        }

        var dels = new List<DiffLine>();
        var adds = new List<DiffLine>();
        void Flush()
        {
            int n = Math.Max(dels.Count, adds.Count);
            for (int i = 0; i < n; i++)
            {
                var l = i < dels.Count ? dels[i] : null;
                var r = i < adds.Count ? adds[i] : null;
                rows.Add(new DiffRow { Kind = DiffRowKind.Change, Left = l, Right = r, Mode = mode, HunkIndex = (l ?? r)!.HunkIndex });
            }
            dels.Clear(); adds.Clear();
        }
        foreach (var l in lines)
        {
            switch (l.Kind)
            {
                case DiffLineKind.Del: dels.Add(l); break;
                case DiffLineKind.Add: adds.Add(l); break;
                case DiffLineKind.Context:
                    Flush();
                    rows.Add(new DiffRow { Kind = DiffRowKind.Context, Left = l, Right = l, Mode = mode, HunkIndex = l.HunkIndex });
                    break;
                case DiffLineKind.Meta:
                    break; // "\ No newline at end of file" — kept in the hunk for patches, not shown
                default:
                    Flush();
                    rows.Add(new DiffRow { Kind = DiffRowKind.Full, Full = l, Mode = mode, HunkIndex = l.HunkIndex });
                    break;
            }
        }
        Flush();
        return rows;
    }

    // ---------------------------------------------------------------- patch building

    /// <summary>
    /// Builds a patch that contains only <paramref name="selected"/> change lines. For a forward apply (stage) the
    /// unselected removals become context and unselected additions vanish; for a reverse apply (discard / unstage)
    /// it is the other way round, because the side being patched already contains the additions.
    /// </summary>
    private string? BuildPatch(HashSet<DiffLine> selected, bool reverse)
    {
        var sb = new StringBuilder();
        foreach (var h in _headers) sb.Append(h).Append('\n');
        int delta = 0;
        bool any = false;
        foreach (var hunk in _hunks)
        {
            if (!hunk.Skip(1).Any(selected.Contains)) continue;
            any = true;
            var m = Regex.Match(hunk[0].Raw, @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$");
            int oldStart = m.Success ? int.Parse(m.Groups[1].Value) : 1;
            int newStart = m.Success ? int.Parse(m.Groups[3].Value) : 1;
            var body = new List<string>();
            bool lastKept = true;
            foreach (var l in hunk.Skip(1))
            {
                switch (l.Kind)
                {
                    case DiffLineKind.Context: body.Add(l.Raw); lastKept = true; break;
                    case DiffLineKind.Meta: if (lastKept) body.Add(l.Raw); break;
                    case DiffLineKind.Add:
                        if (selected.Contains(l)) { body.Add(l.Raw); lastKept = true; }
                        else if (reverse) { body.Add(" " + l.Raw[1..]); lastKept = true; }
                        else lastKept = false;
                        break;
                    case DiffLineKind.Del:
                        if (selected.Contains(l)) { body.Add(l.Raw); lastKept = true; }
                        else if (!reverse) { body.Add(" " + l.Raw[1..]); lastKept = true; }
                        else lastKept = false;
                        break;
                }
            }
            int oldCount = body.Count(x => x.Length > 0 && (x[0] == ' ' || x[0] == '-'));
            int newCount = body.Count(x => x.Length > 0 && (x[0] == ' ' || x[0] == '+'));
            // Forward patches are applied to the old side (its offsets stay valid); reverse patches are applied
            // with -R to the new side, so the "+" offsets are the ones git uses to locate the hunk.
            int oldHdr = reverse ? newStart - delta : oldStart;
            int newHdr = reverse ? newStart : oldStart + delta;
            if (oldCount == 0) oldHdr = Math.Max(0, oldHdr - 1);
            if (newCount == 0) newHdr = Math.Max(0, newHdr - 1);
            sb.Append($"@@ -{oldHdr},{oldCount} +{newHdr},{newCount} @@{(m.Success ? m.Groups[5].Value : "")}\n");
            foreach (var b in body) sb.Append(b).Append('\n');
            delta += newCount - oldCount;
        }
        return any ? sb.ToString() : null;
    }

    private HashSet<DiffLine> SelectedChangeLines()
        => Lines.SelectedItems.OfType<DiffRow>().Where(r => r.HasChange).SelectMany(r => r.Lines).ToHashSet();

    private HashSet<DiffLine> SelectedHunkLines()
    {
        var idx = Lines.SelectedItems.OfType<DiffRow>().Select(r => r.HunkIndex).Where(i => i >= 0).Distinct().ToList();
        if (idx.Count == 0 && _hunks.Count == 1) idx.Add(0);
        return idx.SelectMany(i => _hunks[i].Skip(1)).Where(l => l.Kind is DiffLineKind.Add or DiffLineKind.Del).ToHashSet();
    }

    private async void Request(HunkOp op, HashSet<DiffLine> lines, string what)
    {
        if (lines.Count == 0)
        {
            await Dlg.Info("Nothing selected", $"Select {what} first. Click a row, or Ctrl/Shift-click to select several.");
            return;
        }
        if (op == HunkOp.Discard && !await Dlg.Confirm("Discard lines", $"Discard {lines.Count} changed line(s) from the working tree?\n\nThis cannot be undone.", "Discard", "Keep", danger: true))
            return;
        var patch = BuildPatch(lines, reverse: op != HunkOp.Stage);
        if (patch != null) HunkRequested?.Invoke(op, patch);
    }

    private void StageLines_Click(object s, RoutedEventArgs e) => Request(HunkOp.Stage, SelectedChangeLines(), "changed rows");
    private void UnstageLines_Click(object s, RoutedEventArgs e) => Request(HunkOp.Unstage, SelectedChangeLines(), "changed rows");
    private void DiscardLines_Click(object s, RoutedEventArgs e) => Request(HunkOp.Discard, SelectedChangeLines(), "changed rows");
    private void StageHunk_Click(object s, RoutedEventArgs e) => Request(HunkOp.Stage, SelectedHunkLines(), "a row inside the hunk");
    private void UnstageHunk_Click(object s, RoutedEventArgs e) => Request(HunkOp.Unstage, SelectedHunkLines(), "a row inside the hunk");
    private void DiscardHunk_Click(object s, RoutedEventArgs e) => Request(HunkOp.Discard, SelectedHunkLines(), "a row inside the hunk");

    /// <summary>Gutter arrow on a row: acts on the whole selection if the row is part of it, otherwise on that row alone.</summary>
    private HashSet<DiffLine> RowLines(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is not DiffRow row) return new HashSet<DiffLine>();
        if (Lines.SelectedItems.Contains(row) && Lines.SelectedItems.Count > 1) return SelectedChangeLines();
        return row.Lines.Where(l => l.Kind is DiffLineKind.Add or DiffLineKind.Del).ToHashSet();
    }
    private void RowStage_Click(object s, RoutedEventArgs e) { e.Handled = true; Request(HunkOp.Stage, RowLines(s), "a row"); }
    private void RowUnstage_Click(object s, RoutedEventArgs e) { e.Handled = true; Request(HunkOp.Unstage, RowLines(s), "a row"); }
    private void RowDiscard_Click(object s, RoutedEventArgs e) { e.Handled = true; Request(HunkOp.Discard, RowLines(s), "a row"); }

    // ---------------------------------------------------------------- misc

    private void CopyLeft_Click(object s, RoutedEventArgs e) => Copy(right: false);
    private void CopyRight_Click(object s, RoutedEventArgs e) => Copy(right: true);

    private void Copy(bool right)
    {
        var rows = Lines.SelectedItems.OfType<DiffRow>().OrderBy(_rows.IndexOf).ToList();
        if (rows.Count == 0) return;
        var text = rows.Select(r => r.IsFull ? r.FullText : (right ? r.Right : r.Left)?.Text).Where(t => t != null);
        Clipboard.SetText(string.Join("\n", text));
    }

    private ScrollViewer? Scroller()
    {
        if (VisualTreeHelper.GetChildrenCount(Lines) == 0) return null;
        var border = VisualTreeHelper.GetChild(Lines, 0);
        return VisualTreeHelper.GetChildrenCount(border) > 0 ? VisualTreeHelper.GetChild(border, 0) as ScrollViewer : null;
    }
    private double ScrollOffset() => Scroller()?.VerticalOffset ?? 0;
    private void SetScrollOffset(double v) => Scroller()?.ScrollToVerticalOffset(v);
}
