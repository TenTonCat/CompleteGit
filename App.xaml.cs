using System.Windows;

namespace CompleteGit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (MainWindow is MainWindow mw && mw.IsLoaded) mw.ShowError(args.Exception.ToString());
            else MessageBox.Show(args.Exception.ToString(), "CompleteGit - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }
}
