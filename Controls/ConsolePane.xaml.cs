using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CompleteGit.Shell;

namespace CompleteGit.Controls;

public partial class ConsolePane : UserControl
{
    private readonly ConcurrentQueue<(OutputKind Kind, string Text)> _queue = new();
    private readonly DispatcherTimer _flush;
    private readonly List<string> _history = new();
    private int _histIdx = -1;
    private string _draft = "";
    private Paragraph? _para;
    private int _paraLines;
    private string _shell = "pwsh";

    private static readonly Brush CommandBrush = Freeze(Color.FromRgb(0x4F, 0xC1, 0xFF));
    private static readonly Brush StdoutBrush = Freeze(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly Brush StderrBrush = Freeze(Color.FromRgb(0xCE, 0x91, 0x78));
    private static readonly Brush InfoBrush = Freeze(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly Brush ErrorBrush = Freeze(Color.FromRgb(0xF4, 0x87, 0x71));
    private static readonly Brush SummaryBrush = Freeze(Color.FromRgb(0x6A, 0x99, 0x55));
    private static readonly Brush QueryBrush = Freeze(Color.FromRgb(0x3A, 0x7C, 0xA8));
    private static readonly Brush QuerySummaryBrush = Freeze(Color.FromRgb(0x55, 0x66, 0x55));
    private static readonly Brush QueryStderrBrush = Freeze(Color.FromRgb(0x8A, 0x6A, 0x55));
    private bool _showQueries = true;
    public event Action<bool>? ShowQueriesChanged;
    public bool QueriesVisible { get => _showQueries; set { _showQueries = value; ShowQueries.IsChecked = value; } }
    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public event Action<string>? CommandSubmitted;
    public event Action? StopRequested;
    public event Action<string>? ShellChanged;

    public string Shell
    {
        get => _shell;
        set { _shell = value; BtnShell.Content = $"Shell: {value} ▾"; }
    }

    public ConsolePane()
    {
        InitializeComponent();
        Shell = "pwsh";
        _flush = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        _flush.Tick += Flush;
    }

    /// <summary>Thread-safe: output is queued and painted in batches on the UI thread.</summary>
    public void Append(OutputKind kind, string text)
    {
        _queue.Enqueue((kind, text));
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (!_flush.IsEnabled) _flush.Start(); });
    }

    public void SetPrompt(string cwd) => PromptText.Text = cwd + ">";

    public void SetBusy(bool busy)
    {
        BtnStop.IsEnabled = busy;
        BusyText.Text = busy ? "● running" : "";
    }

    public void FocusInput() => Input.Focus();

    public void Clear()
    {
        Doc.Blocks.Clear();
        _para = null;
        _paraLines = 0;
    }

    private void Flush(object? sender, EventArgs e)
    {
        if (_queue.IsEmpty) { _flush.Stop(); return; }
        bool atBottom = Out.VerticalOffset >= Out.ExtentHeight - Out.ViewportHeight - 6;
        int n = 0;
        while (n++ < 3000 && _queue.TryDequeue(out var item))
        {
            if (item.Kind == OutputKind.Clear) { Clear(); continue; }
            if (!_showQueries && item.Kind is OutputKind.Query or OutputKind.QuerySummary or OutputKind.QueryStderr) continue;
            if (_para == null || _paraLines >= 200)
            {
                _para = new Paragraph { Margin = new Thickness(0) };
                _paraLines = 0;
                Doc.Blocks.Add(_para);
                while (Doc.Blocks.Count > 120) Doc.Blocks.Remove(Doc.Blocks.FirstBlock);
            }
            var run = new Run(item.Text);
            switch (item.Kind)
            {
                case OutputKind.Command: run.Foreground = CommandBrush; run.FontWeight = FontWeights.Bold; break;
                case OutputKind.Query: run.Foreground = QueryBrush; break;
                case OutputKind.QuerySummary: run.Foreground = QuerySummaryBrush; break;
                case OutputKind.QueryStderr: run.Foreground = QueryStderrBrush; break;
                case OutputKind.Stderr: run.Foreground = StderrBrush; break;
                case OutputKind.Info: run.Foreground = InfoBrush; run.FontStyle = FontStyles.Italic; break;
                case OutputKind.Error: run.Foreground = ErrorBrush; break;
                case OutputKind.Summary: run.Foreground = SummaryBrush; break;
                default: run.Foreground = StdoutBrush; break;
            }
            _para.Inlines.Add(run);
            _para.Inlines.Add(new LineBreak());
            _paraLines++;
        }
        if (atBottom) Out.ScrollToEnd();
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                {
                    var text = Input.Text;
                    Input.Clear();
                    _histIdx = -1; _draft = "";
                    if (text.Trim().Length > 0)
                    {
                        if (_history.Count == 0 || _history[^1] != text) _history.Add(text);
                        CommandSubmitted?.Invoke(text);
                    }
                    e.Handled = true;
                    break;
                }
            case Key.Up:
                if (_history.Count == 0) break;
                if (_histIdx == -1) { _draft = Input.Text; _histIdx = _history.Count; }
                if (_histIdx > 0) _histIdx--;
                Input.Text = _history[_histIdx];
                Input.CaretIndex = Input.Text.Length;
                e.Handled = true;
                break;
            case Key.Down:
                if (_histIdx == -1) break;
                _histIdx++;
                if (_histIdx >= _history.Count) { _histIdx = -1; Input.Text = _draft; }
                else Input.Text = _history[_histIdx];
                Input.CaretIndex = Input.Text.Length;
                e.Handled = true;
                break;
            case Key.Escape:
                Input.Clear();
                e.Handled = true;
                break;
            case Key.L when Keyboard.Modifiers == ModifierKeys.Control:
                Clear();
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control && Input.SelectionLength == 0 && BtnStop.IsEnabled:
                StopRequested?.Invoke();
                e.Handled = true;
                break;
        }
    }

    private void BtnShell_Click(object sender, RoutedEventArgs e)
    {
        Shell = Shell switch { "pwsh" => "powershell", "powershell" => "cmd", _ => "pwsh" };
        ShellChanged?.Invoke(Shell);
        Append(OutputKind.Info, $"Shell for typed commands: {Shell}");
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void ShowQueries_Click(object sender, RoutedEventArgs e)
    {
        bool v = ShowQueries.IsChecked == true;
        if (v == _showQueries) return;
        _showQueries = v;
        ShowQueriesChanged?.Invoke(v);
    }
    private void BtnClear_Click(object sender, RoutedEventArgs e) => Clear();
}
