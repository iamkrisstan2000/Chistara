using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Chistara;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private static string LogPath
    {
        get
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chistara");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "chistara.log");
        }
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var message = new StringBuilder()
                .Append('[')
                .Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture))
                .AppendLine("] Chistara error")
                .AppendLine(exception.ToString())
                .AppendLine(new string('-', 72))
                .ToString();
            File.AppendAllText(LogPath, message);
        }
        catch
        {
            // Logging must never make a recoverable UI error fatal.
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show(
            "Chistara recovered from an unexpected problem. Details were written to your local Chistara log.",
            "Chistara",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            WriteCrashLog(exception);
    }
}
