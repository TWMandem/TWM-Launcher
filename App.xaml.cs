using System;
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
            // If the OS cannot create the mutex, do not prevent the launcher
            // from opening. MainWindow still guards duplicate launches itself.
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
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex may not have been acquired if startup was interrupted.
        }
        finally
        {
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        }

        base.OnExit(e);
    }
}
