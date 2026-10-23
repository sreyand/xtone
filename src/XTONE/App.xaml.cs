namespace XTONE;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            MessageBox.Show($"XTONE encountered an unexpected error and needs to close.\n\n{args.Exception.Message}\n\nDetails were saved to last-crash.log in the library storage folder.",
                "XTONE", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) WriteCrashLog(error);
        };
        base.OnStartup(e);
    }

    private static void WriteCrashLog(Exception error)
    {
        try
        {
            var root = Environment.GetEnvironmentVariable("XTONE_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTONE");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "last-crash.log"), $"{DateTimeOffset.Now:O}\n{error}");
        }
        catch { /* An inaccessible storage folder must not hide the original error. */ }
    }
}
