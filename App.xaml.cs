
using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace AMM.App;

public partial class App : Application
{
    private static Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = @"Local\TWMLauncher_SingleInstance_2026";
        bool createdNew;

        try
        {
            _instanceMutex = new Mutex(true, mutexName, out createdNew);
        }
        catch (Exception)
        {
            createdNew = true;
        }

        if (!createdNew)
        {
            SilverMessageBox.Show(
                "TWM Launcher уже запущен. Переключись на открытое окно лаунчера.",
                "TWM Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        MainWindow = new MainWindow(GetUpdateConfirmationPath(e.Args));
        MainWindow.Show();
    }

    private static string? GetUpdateConfirmationPath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(
                    args[i], "--twm-update-confirm",
                    StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                string suppliedPath = Path.GetFullPath(args[i + 1]);

                string updatesRoot = Path.GetFullPath(
                    Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                        "TWM Launcher",
                        "Updates"));

                string rootPrefix =
                    updatesRoot.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                string fileName = Path.GetFileName(suppliedPath);

                if (suppliedPath.StartsWith(
                        rootPrefix, StringComparison.OrdinalIgnoreCase)
                    && fileName.StartsWith(
                        "confirm-", StringComparison.OrdinalIgnoreCase)
                    && fileName.EndsWith(
                        ".ok", StringComparison.OrdinalIgnoreCase))
                {
                    return suppliedPath;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Mutex might not have been acquired if startup was interrupted.
        }
        finally
        {
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        }

        base.OnExit(e);
    }
}
