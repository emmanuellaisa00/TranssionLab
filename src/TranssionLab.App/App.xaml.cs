using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using TranssionLab.Core.Interfaces;
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

        // Wire native Windows dialogs and clipboard into ViewModel bridge
        UiInteractionBridge.PickOpenFile = (title, filter) =>
        {
            var dlg = new OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        };

        UiInteractionBridge.PickDirectory = title =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = title
            };
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        };

        UiInteractionBridge.CopyTextToClipboard = text =>
        {
            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetText(text);
            }
        };

        var services = new ServiceCollection();
        services.AddTranssionLabServices();
        services.AddSingleton<MainWindow>();
        _serviceProvider = services.BuildServiceProvider();

        var logger = _serviceProvider.GetRequiredService<IStructuredLogger>();

        // Local crash reporting (Section 8) — never sends telemetry over the network
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
                $"An unexpected error occurred and was logged locally to:\n{path}\n\n{args.Exception.Message}",
                "TranssionLab Diagnostic Error",
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
        if (_serviceProvider is not null)
        {
            try
            {
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
