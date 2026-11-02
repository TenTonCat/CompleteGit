using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CompleteGit.Controls;

public sealed record DialogField(string Label, string Default = "", bool Multiline = false);
public sealed record DialogResult(bool Ok, string[] Values, bool Checked, int Choice = -1);

/// <summary>
/// In-window replacement for MessageBox / modal input windows: a dimmed overlay with a card.
/// No system sound, no extra top-level window, focus stays inside the app and is restored afterwards.
/// Requests are queued so two prompts never overlap.
/// </summary>
public partial class DialogHost : UserControl
{
    public static DialogHost? Current { get; private set; }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<TextBox> _boxes = new();
    private TaskCompletionSource<bool>? _tcs;
    private int _choice = -1;
    private IInputElement? _prevFocus;

    public bool IsOpen => Visibility == Visibility.Visible;

    public DialogHost()
    {
        InitializeComponent();
        Current = this;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string okText = "Yes", string cancelText = "No", bool danger = false)
        => (await ShowAsync(title, message, null, null, false, okText, cancelText, danger)).Ok;

    public async Task InfoAsync(string title, string message)
        => await ShowAsync(title, message, null, null, false, "OK", null, false);

    public async Task<DialogResult?> InputAsync(string title, IList<DialogField> fields, string? checkLabel = null, bool checkDefault = false, string okText = "OK", string message = "")
    {
        var r = await ShowAsync(title, message, fields, checkLabel, checkDefault, okText, "Cancel", false);
        return r.Ok ? r : null;
    }

    public async Task<string?> AskAsync(string title, string label, string def = "", string okText = "OK")
    {
        var r = await InputAsync(title, new[] { new DialogField(label, def) }, okText: okText);
        return r?.Values[0];
    }

    /// <summary>One full-width button per choice plus Cancel. Returns the index picked, or -1 for Cancel/Escape.</summary>
    public async Task<int> ChooseAsync(string title, string message, params (string Text, bool Danger)[] choices)
    {
        var r = await ShowAsync(title, message, null, null, false, "", "Cancel", false, choices);
        return r.Ok ? r.Choice : -1;
    }

    private async Task<DialogResult> ShowAsync(string title, string message, IList<DialogField>? fields, string? checkLabel, bool checkDefault,
        string okText, string? cancelText, bool danger, IList<(string Text, bool Danger)>? choices = null)
    {
        await _gate.WaitAsync();
        try
        {
            _prevFocus = Keyboard.FocusedElement;
            TitleText.Text = title;
            MessageText.Text = message;
            MessageText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Fields.Children.Clear();
            _boxes.Clear();
            if (fields != null)
            {
                foreach (var f in fields)
                {
                    Fields.Children.Add(new TextBlock { Text = f.Label, Margin = new Thickness(0, 10, 0, 4), Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush") });
                    var tb = new TextBox { Text = f.Default };
                    if (f.Multiline)
                    {
                        tb.AcceptsReturn = true;
                        tb.MinHeight = 80;
                        tb.TextWrapping = TextWrapping.Wrap;
                        tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    }
                    _boxes.Add(tb);
                    Fields.Children.Add(tb);
                }
            }
            Check.Visibility = checkLabel != null ? Visibility.Visible : Visibility.Collapsed;
            Check.Content = checkLabel ?? "";
            Check.IsChecked = checkDefault;
            Choices.Children.Clear();
            _choice = -1;
            if (choices != null)
            {
                for (int i = 0; i < choices.Count; i++)
                {
                    int index = i;
                    var btn = new Button
                    {
                        Content = choices[i].Text, Tag = index, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(12, 7, 12, 7),
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                    };
                    if (choices[i].Danger) btn.Style = (Style)FindResource("DangerButton");
                    btn.Click += (_, _) => { _choice = index; Close(true); };
                    Choices.Children.Add(btn);
                }
            }
            Choices.Visibility = choices != null ? Visibility.Visible : Visibility.Collapsed;
            BtnOk.Visibility = choices != null ? Visibility.Collapsed : Visibility.Visible;
            BtnOk.Content = okText;
            BtnOk.Style = (Style)FindResource(danger ? "DangerButton" : "AccentButton");
            BtnCancel.Visibility = cancelText != null ? Visibility.Visible : Visibility.Collapsed;
            BtnCancel.Content = cancelText ?? "";

            Visibility = Visibility.Visible;
            _tcs = new TaskCompletionSource<bool>();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (_boxes.Count > 0) { _boxes[0].Focus(); _boxes[0].SelectAll(); }
                else if (Choices.Children.Count > 0) ((Button)Choices.Children[0]).Focus();
                else BtnOk.Focus();
            });
            bool ok = await _tcs.Task;
            var result = new DialogResult(ok, _boxes.Select(b => b.Text).ToArray(), Check.IsChecked == true, _choice);
            Visibility = Visibility.Collapsed;
            try { _prevFocus?.Focus(); } catch { /* element may be gone */ }
            return result;
        }
        finally { _gate.Release(); }
    }

    private void Close(bool ok) => _tcs?.TrySetResult(ok);

    private void Ok_Click(object sender, RoutedEventArgs e) => Close(true);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close(false);
    private void Card_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (BtnCancel.Visibility == Visibility.Visible) Close(false);
    }

    private void Card_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(BtnCancel.Visibility != Visibility.Visible); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            if (Keyboard.FocusedElement is TextBox { AcceptsReturn: true } && Keyboard.Modifiers != ModifierKeys.Control) return;
            if (ReferenceEquals(Keyboard.FocusedElement, BtnCancel)) { Close(false); e.Handled = true; return; }
            if (Choices.Children.Count > 0)
            {
                // Enter only picks a choice that has focus; there is no default when the answers differ this much
                if (Keyboard.FocusedElement is Button { Tag: int i }) { _choice = i; Close(true); e.Handled = true; }
                return;
            }
            Close(true);
            e.Handled = true;
        }
    }
}

/// <summary>Static shortcuts so controls other than the main window can prompt too.</summary>
public static class Dlg
{
    public static Task<bool> Confirm(string title, string message, string okText = "Yes", string cancelText = "No", bool danger = false)
        => DialogHost.Current?.ConfirmAsync(title, message, okText, cancelText, danger)
           ?? Task.FromResult(MessageBox.Show(message, title, MessageBoxButton.YesNo) == MessageBoxResult.Yes);

    public static Task Info(string title, string message)
        => DialogHost.Current?.InfoAsync(title, message) ?? Task.Run(() => MessageBox.Show(message, title));
}
