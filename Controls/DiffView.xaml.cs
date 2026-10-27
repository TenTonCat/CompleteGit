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
public sealed class DiffRow : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private bool _isEditing;
    private string _editText = "";
    /// <summary>True while the right (working tree) side shows the inline editor.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set { _isEditing = value; PropertyChanged?.Invoke(this, new(nameof(IsEditing))); }
    }
    public string EditText
    {
        get => _editText;
        set { _editText = value; PropertyChanged?.Invoke(this, new(nameof(EditText))); }
    }

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
    /// <summary>Working-tree file behind the right side, when its lines may be edited in place; null otherwise.</summary>
    private string? _workFile;
    private DiffRow? _editing;
    /// <summary>A refresh of the file being edited, held back until the edit ends so it doesn't throw the edit away.</summary>
    private (string Title, string Diff, DiffMode Mode, string? WorkFile)? _pendingShow;

    public event Action<HunkOp, string>? HunkRequested;
    /// <summary>Raised after an in-place edit has been written to the working-tree file.</summary>
    public event Action? FileEdited;
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
            if (e.OriginalSource is TextBox) return;
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { Copy(right: true); e.Handled = true; }
            else if (e.Key == Key.F2 && Lines.SelectedItem is DiffRow row && CanEdit(row)) { BeginEdit(row); e.Handled = true; }
        };
        Lines.ContextMenuOpening += (_, _) =>
        {
            var u = _mode == DiffMode.Unstaged ? Visibility.Visible : Visibility.Collapsed;
            var s = _mode == DiffMode.Staged ? Visibility.Visible : Visibility.Collapsed;
            MenuStageLines.Visibility = u; MenuDiscardLines.Visibility = u; MenuStageHunk.Visibility = u; MenuDiscardHunk.Visibility = u;
            MenuUnstageLines.Visibility = s; MenuUnstageHunk.Visibility = s;
            var ed = CanEdit(Lines.SelectedItem as DiffRow) ? Visibility.Visible : Visibility.Collapsed;
            MenuEditLine.Visibility = ed; MenuDeleteLine.Visibility = ed; MenuEditSep.Visibility = ed;
        };
        Lines.SizeChanged += (_, _) => UpdateHBar();
        Lines.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Shift || !HBar.IsVisible) return;
            HBar.Value = Math.Clamp(HBar.Value - e.Delta / 2.0, 0, HBar.Maximum);
            e.Handled = true;
        };
        ApplyLayout();
    }

    public void Clear()
    {
        if (_editing != null) { _editing.IsEditing = false; _editing = null; }
        _pendingShow = null;
        _workFile = null;
        _mode = DiffMode.None;
        _title = "";
        TitleText.Text = "Diff";
        StatsText.Text = "";
        LeftTitle.Text = ""; RightTitle.Text = "";
        SideTitles.Visibility = Visibility.Collapsed;
        Lines.ItemsSource = null;
        _headers = new(); _lines = new(); _hunks = new(); _rows = new();
        UpdateButtons();
        UpdateHBar();
    }

    public void ShowMessage(string title, string message)
    {
        Clear();
        TitleText.Text = title;
        Lines.ItemsSource = new List<DiffRow> { new() { Kind = DiffRowKind.Full, Full = new DiffLine { Kind = DiffLineKind.Meta, Text = message } } };
    }

    /// <param name="workFile">Full path of the working-tree file on the right side, to allow editing its lines in place.</param>
    public void Show(string title, string diff, DiffMode mode, string? workFile = null)
    {
        bool sameFile = title == _title && Lines.ItemsSource != null;
        if (_editing != null)
        {
            if (sameFile) { _pendingShow = (title, diff, mode, workFile); return; }
            EndEdit(save: true);
        }
        _pendingShow = null;
        double offset = sameFile ? ScrollOffset() : 0;

        _mode = mode;
        _title = title;
        _workFile = workFile;
        TitleText.Text = title;
        (LeftTitle.Text, RightTitle.Text) = mode switch
        {
            DiffMode.Unstaged => ("Index (staged)   ◀ stage      discard ▶", "Working tree"),
            DiffMode.Staged => ("HEAD", "Index (staged)   unstage ▶"),
            _ => ("Before", workFile != null ? "Working tree" : "After"),
        };
        if (workFile != null) RightTitle.Text += "   (double-click a line to edit)";

        SideTitles.Visibility = _sideBySide ? Visibility.Visible : Visibility.Collapsed;
        var (lines, headers, hunks, adds, dels) = Parse(diff);
        _lines = lines; _headers = headers; _hunks = hunks;
        if (lines.Count == 0) lines.Add(new DiffLine { Kind = DiffLineKind.Meta, Text = "(no differences)" });
        _rows = BuildRows(lines, mode);
        Lines.ItemsSource = _rows;
        StatsText.Text = adds + dels > 0 ? $"+{adds}  −{dels}" : "";
        UpdateButtons();
        if (!sameFile) HBar.Value = 0;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, UpdateHBar);

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
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, UpdateHBar);
    }

    private double _charWidth;

    /// <summary>
    /// Side by side, each half is a fixed-width column, so one scroll bar under both shifts their text together.
    /// It's sized from the longest line on either side (the font is monospaced).
    /// </summary>
    private void UpdateHBar()
    {
        int longest = _sideBySide ? _rows.Where(r => r.IsPair).Select(r => Math.Max(r.LeftText.Length, r.RightText.Length)).DefaultIfEmpty(0).Max() : 0;
        if (_charWidth == 0)
        {
            var ft = new FormattedText(new string('0', 100), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(Lines.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), Lines.FontSize, Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            _charWidth = ft.WidthIncludingTrailingWhitespace / 100;
        }
        // Row columns are 40 | * | 44 | 40 | *, and each text cell has 10px of margin.
        double viewport = Scroller()?.ViewportWidth is > 0 and var w ? w : Lines.ActualWidth;
        double column = (viewport - 124) / 2 - 10;
        double overflow = longest * _charWidth + 8 - column;
        if (column <= 0 || overflow <= 0)
        {
            HBar.Visibility = Visibility.Collapsed;
            HBar.Value = 0;
            return;
        }
        HBar.Visibility = Visibility.Visible;
        HBar.Maximum = overflow;
        HBar.ViewportSize = column;
        HBar.LargeChange = column * 0.8;
    }

    private void HBar_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        => ((TranslateTransform)Resources["HShift"]).X = -e.NewValue;

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

    // ---------------------------------------------------------------- in-place editing

    /// <summary>Lines that exist in the working-tree file (added or context) can be edited when the right side is that file.</summary>
    private bool CanEdit(DiffRow? row)
        => _workFile != null && row?.Right is { Kind: DiffLineKind.Add or DiffLineKind.Context } r && int.TryParse(r.NewNo, out _);

    /// <summary>The line as it is in the file: the diff's raw text, so tabs aren't expanded.</summary>
    private static string FileText(DiffLine l) => l.Raw.Length > 0 ? l.Raw[1..] : "";

    private void BeginEdit(DiffRow row)
    {
        if (_editing == row) return;
        EndEdit(save: true);
        if (!CanEdit(row)) return;
        _editing = row;
        row.EditText = FileText(row.Right!);
        row.IsEditing = true;
        Lines.SelectedItems.Clear();
        Lines.SelectedItem = row;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (_editing == row && Lines.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject c && FindChild<TextBox>(c) is { } tb)
            {
                tb.Focus();
                tb.CaretIndex = tb.Text.Length;
            }
        });
    }

    private void EndEdit(bool save)
    {
        if (_editing is not { } row) return;
        _editing = null;
        row.IsEditing = false;
        var original = FileText(row.Right!);
        if (save && row.EditText != original)
            Save(int.Parse(row.Right!.NewNo), original, row.EditText);
        if (Lines.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item && IsKeyboardFocusWithin) item.Focus();
        if (_pendingShow is { } p) Show(p.Title, p.Diff, p.Mode, p.WorkFile);
    }

    private void Save(int lineNo, string expected, string? replacement)
    {
        try { ReplaceLine(_workFile!, lineNo, expected, replacement); }
        catch (Exception ex) { _ = Dlg.Info("Edit not saved", ex.Message); }
        FileEdited?.Invoke();
    }

    /// <summary>
    /// Replaces (or with null, removes) one line of a UTF-8 file, keeping its BOM and line endings.
    /// Refuses if the line no longer matches what the diff showed, so a stale diff can't clobber newer changes.
    /// </summary>
    private static void ReplaceLine(string path, int lineNo, string expected, string? replacement)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        bool bom = bytes is [0xEF, 0xBB, 0xBF, ..];
        var enc = new UTF8Encoding(bom, throwOnInvalidBytes: true);
        string text;
        try { text = enc.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)); }
        catch (DecoderFallbackException) { throw new InvalidOperationException("The file isn't UTF-8, so it can't be edited here safely."); }

        var lines = text.Split('\n');
        int i = lineNo - 1;
        if (i < 0 || i >= lines.Length || lines[i].TrimEnd('\r') != expected.TrimStart('﻿'))
            throw new InvalidOperationException($"Line {lineNo} no longer matches the diff; the file was changed elsewhere. The diff has been refreshed, so try again.");

        var cr = lines[i].EndsWith('\r') ? "\r" : "";
        if (replacement == null)
        {
            var list = lines.ToList();
            list.RemoveAt(i);
            // Removing an unterminated last line leaves the line before it as the new unterminated last line.
            if (i == list.Count && i > 0) list[i - 1] = list[i - 1].TrimEnd('\r');
            lines = list.ToArray();
        }
        else
        {
            var eol = cr.Length > 0 || text.Contains("\r\n") ? "\r\n" : "\n";
            lines[i] = replacement.Replace("\r\n", "\n").Replace("\n", eol) + cr;
        }
        System.IO.File.WriteAllBytes(path, [.. enc.GetPreamble(), .. enc.GetBytes(string.Join('\n', lines))]);
    }

    private void Lines_MouseDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || FindParent<TextBox>(d) != null || FindParent<Button>(d) != null) return;
        if (ItemsControl.ContainerFromElement(Lines, d) is not ListBoxItem item || item.DataContext is not DiffRow row || !CanEdit(row)) return;
        // Side by side, only the right (working tree) half edits; it starts past the middle gutter.
        if (_sideBySide && e.GetPosition(item).X < item.ActualWidth / 2 + 22) return;
        e.Handled = true;
        BeginEdit(row);
    }

    private void EditBox_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; EndEdit(save: false); }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; EndEdit(save: true); }
    }

    private void EditBox_LostKeyboardFocus(object s, KeyboardFocusChangedEventArgs e)
    {
        // Clicking elsewhere saves, like a spreadsheet cell; a context menu opening over the box doesn't count.
        if (e.NewFocus is DependencyObject d && FindParent<ContextMenu>(d) != null) return;
        if ((s as FrameworkElement)?.DataContext == _editing) EndEdit(save: true);
    }

    private void EditLine_Click(object s, RoutedEventArgs e)
    {
        if (Lines.SelectedItem is DiffRow row) BeginEdit(row);
    }

    private void DeleteLine_Click(object s, RoutedEventArgs e)
    {
        if (Lines.SelectedItem is not DiffRow row || !CanEdit(row)) return;
        EndEdit(save: true);
        Save(int.Parse(row.Right!.NewNo), FileText(row.Right), null);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

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
