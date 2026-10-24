using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using TranssionLab.Core.Interfaces;
using TranssionLab.Firmware;
using TranssionLab.Logging;
using TranssionLab.ViewModels;
using TranssionLab.ViewModels.Mvvm;

namespace TranssionLab.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Marshal all cross-thread ViewModel property/collection/command notifications onto WPF Dispatcher
        UiInteractionBridge.InvokeOnUiThread = action =>
        {
            var disp = Current?.Dispatcher;
            if (disp is null || disp.CheckAccess())
            {
                action();
                return;
            }

            if (!disp.HasShutdownStarted && !disp.HasShutdownFinished)
            {
                disp.Invoke(DispatcherPriority.Normal, action);
            }
        };

        // Wire native Windows dialogs and clipboard into ViewModel bridge with full defensive guards
        UiInteractionBridge.PickOpenFile = (title, filter) =>
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = title,
                    Filter = filter,
                    CheckFileExists = true
                };
                return dlg.ShowDialog() == true ? dlg.FileName : null;
            }
            catch
            {
                return null;
            }
        };

        UiInteractionBridge.PickSaveFile = (title, filter, defaultFileName) =>
        {
            try
            {
                var ext = Path.GetExtension(defaultFileName);
                var stem = HashAndBinaryHelpers.SanitizeFileNameComponent(Path.GetFileNameWithoutExtension(defaultFileName), "export");
                var dlg = new SaveFileDialog
                {
                    Title = title,
                    Filter = filter,
                    FileName = string.IsNullOrEmpty(ext) ? stem : $"{stem}{ext}"
                };
                return dlg.ShowDialog() == true ? dlg.FileName : null;
            }
            catch
            {
                return null;
            }
        };

        UiInteractionBridge.PickDirectory = title =>
        {
            try
            {
                var dlg = new OpenFolderDialog
                {
                    Title = title
                };
                return dlg.ShowDialog() == true ? dlg.FolderName : null;
            }
            catch
            {
                return null;
            }
        };

        UiInteractionBridge.CopyTextToClipboard = text =>
        {
            if (string.IsNullOrEmpty(text))
                return;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, false);
                    return;
                }
                catch (COMException)
                {
                    Thread.Sleep(40);
                }
                catch
                {
                    return;
                }
            }
        };

        var services = new ServiceCollection();
        services.AddTranssionLabServices();
        services.AddSingleton<MainWindow>();
        _serviceProvider = services.BuildServiceProvider();

        var logger = _serviceProvider.GetRequiredService<IStructuredLogger>();

        UiInteractionBridge.OnCommandException = ex =>
        {
            logger.LogError("UiCommandGuard", ex.GetType().Name, ex.Message);
        };

        // Local crash & unobserved task protection (Section 8) — never sends telemetry over the network
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.LogWarning("TaskScheduler", "UnobservedTaskException", args.Exception.Flatten().Message);
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                CrashReportService.WriteCrashReport(ex, logger.CurrentSessionId);
            }
        };

        DispatcherUnhandledException += (_, args) =>
        {
            var path = CrashReportService.WriteCrashReport(args.Exception, logger.CurrentSessionId);
            logger.LogError("WPFDispatcher", "UnhandledUiException", $"Saved crash report to {path}: {args.Exception.Message}");
            MessageBox.Show(
                $"An unexpected error was caught safely and logged locally to:\n{path}\n\n{args.Exception.Message}",
                "TranssionLab Diagnostic Guard",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        };

        var settings = _serviceProvider.GetRequiredService<IAppSettingsService>();
        var usb = _serviceProvider.GetRequiredService<IUsbInterfaceProvider>();
        if (settings.Current.AutoPollUsbChanges)
        {
            usb.StartMonitoring();
        }

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UiInteractionBridge.InvokeOnUiThread = null;
        if (_serviceProvider is not null)
        {
            try
            {
                _serviceProvider.GetRequiredService<IDeviceDiscoveryProvider>().CancelActiveWait();
                _serviceProvider.GetRequiredService<IUsbInterfaceProvider>().StopMonitoring();
            }
            catch
            {
                // Ignore shutdown cleanup exceptions
            }
            _serviceProvider.Dispose();
        }
        base.OnExit(e);
    }
}
