using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Core.Services;
using TranssionLab.Firmware;
using TranssionLab.Logging;
using TranssionLab.Profiles;
using TranssionLab.Protocols.Android;
using TranssionLab.Protocols.Diagnostics;
using TranssionLab.Protocols.Discovery;
using TranssionLab.Protocols.MediaTek;
using TranssionLab.Protocols.Transport;
using TranssionLab.Protocols.Unisoc;
using TranssionLab.Protocols.Usb;
using TranssionLab.Servicing;
using TranssionLab.ViewModels.Mvvm;
using TranssionLab.ViewModels.Pages;

namespace TranssionLab.ViewModels;

public sealed class MainShellViewModel : ObservableObject
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IStructuredLogger _logger;

    public ObservableCollection<PageViewModelBase> Pages { get; } = new();
    public ObservableCollection<ConnectedDeviceSnapshot> ConnectedDevices { get; } = new();

    private PageViewModelBase _currentPage;
    public PageViewModelBase CurrentPage
    {
        get => _currentPage;
        set
        {
            if (SetProperty(ref _currentPage, value))
            {
                _ = value.OnNavigatedToAsync();
            }
        }
    }

    private ConnectedDeviceSnapshot? _selectedDeviceItem;
    public ConnectedDeviceSnapshot? SelectedDeviceItem
    {
        get => _selectedDeviceItem;
        set
        {
            if (SetProperty(ref _selectedDeviceItem, value))
            {
                _discovery.SelectDeviceExplicitly(value?.DeviceKey);
                OnPropertyChanged(nameof(RequiresExplicitSelectionBanner));
                OnPropertyChanged(nameof(GlobalConnectionHeadline));
            }
        }
    }

    public bool RequiresExplicitSelectionBanner => _discovery.RequiresExplicitSelection;

    public string GlobalConnectionHeadline
    {
        get
        {
            if (_discovery.RequiresExplicitSelection)
                return $"AMBIGUOUS ({_discovery.ConnectedDevices.Count} devices attached — select target explicitly)";
            var sel = _discovery.SelectedDevice;
            if (sel is null)
                return "No active device connected";
            return $"{sel.ExactModel.DisplayText} [{sel.ActiveConnectionMode}] — {sel.DashboardState}";
        }
    }

    private string _latestLogLine = "Session initialized (Offline-First Mode)";
    public string LatestLogLine
    {
        get => _latestLogLine;
        private set => SetProperty(ref _latestLogLine, value);
    }

    public AsyncRelayCommand GlobalScanDevicesCommand { get; }
    public RelayCommand NavigateToPageCommand { get; }

    public MainShellViewModel(
        IDeviceDiscoveryProvider discovery,
        IStructuredLogger logger,
        DashboardViewModel p1,
        ConnectionManagerViewModel p2,
        DeviceExplorerViewModel p3,
        AdbManagerViewModel p4,
        FastbootManagerViewModel p5,
        MediaTekToolsViewModel p6,
        MetaModeViewModel p7,
        SpdUnisocToolsViewModel p8,
        DiagDiagnosticsViewModel p9,
        BootloaderManagerViewModel p10,
        FirmwareExplorerViewModel p11,
        RomStudioViewModel p12,
        PartitionManagerViewModel p13,
        FlashManagerViewModel p14,
        DeviceProfilesViewModel p15,
        OperationHistoryViewModel p16,
        LiveLogsViewModel p17,
        SettingsViewModel p18,
        DocumentationAboutViewModel p19)
    {
        _discovery = discovery;
        _logger = logger;

        PageViewModelBase[] allPages = [p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17, p18, p19];
        foreach (var page in allPages.OrderBy(p => p.PageNumber))
        {
            Pages.Add(page);
        }
        _currentPage = Pages[0];

        GlobalScanDevicesCommand = new AsyncRelayCommand(async () =>
        {
            await _discovery.RefreshInventoryAsync().ConfigureAwait(true);
            SyncDevicePicker();
        });

        NavigateToPageCommand = new RelayCommand(param =>
        {
            if (param is PageViewModelBase target)
            {
                CurrentPage = target;
            }
            else if (param is int num && Pages.FirstOrDefault(p => p.PageNumber == num) is { } byNum)
            {
                CurrentPage = byNum;
            }
            else if (param is string numStr && int.TryParse(numStr, out var parsed) && Pages.FirstOrDefault(p => p.PageNumber == parsed) is { } byStr)
            {
                CurrentPage = byStr;
            }
        });

        _discovery.InventoryChanged += (_, _) => SyncDevicePicker();
        _logger.EntryRecorded += (_, entry) =>
        {
            LatestLogLine = $"[{entry.TimestampUtc:HH:mm:ss}] [{entry.Severity}] {entry.ProtocolProvider}: {entry.RequestedAction}";
        };

        SyncDevicePicker();
    }

    private void SyncDevicePicker()
    {
        ConnectedDevices.Clear();
        foreach (var d in _discovery.ConnectedDevices)
            ConnectedDevices.Add(d);

        var active = _discovery.SelectedDevice;
        _selectedDeviceItem = active is null
            ? null
            : ConnectedDevices.FirstOrDefault(c => c.DeviceKey == active.DeviceKey);

        OnPropertyChanged(nameof(SelectedDeviceItem));
        OnPropertyChanged(nameof(RequiresExplicitSelectionBanner));
        OnPropertyChanged(nameof(GlobalConnectionHeadline));
    }
}

public static class ServiceRegistration
{
    public static IServiceCollection AddTranssionLabServices(
        this IServiceCollection services,
        string? customDataDirectory = null,
        IWindowsPnpBackend? customPnpBackend = null,
        IProcessRunner? customProcessRunner = null,
        ISerialChannelFactory? customSerialFactory = null)
    {
        var settingsSvc = new AppSettingsService(customDataDirectory);
        var baseDir = settingsSvc.Current.WorkspaceDataDirectory;

        services.AddSingleton<IAppSettingsService>(settingsSvc);
        services.AddSingleton<IStructuredLogger>(_ => new StructuredLogger(
            Path.Combine(baseDir, "logs"),
            () => settingsSvc.Current.RedactSensitiveDataInLogs));
        services.AddSingleton<IOperationHistoryStore>(_ => new OperationHistoryStore(Path.Combine(baseDir, "history")));
        services.AddSingleton<IDeviceProfileRepository>(_ => new DeviceProfileRepository(Path.Combine(baseDir, "profiles")));
        services.AddSingleton<IOperationRegistry, OperationRegistry>();

        services.AddSingleton<IWindowsPnpBackend>(_ => customPnpBackend ?? new WindowsWmiPnpBackend());
        services.AddSingleton<IProcessRunner>(_ => customProcessRunner ?? new SystemProcessRunner());
        services.AddSingleton<ISerialChannelFactory>(_ => customSerialFactory ?? new SystemSerialChannelFactory());

        services.AddSingleton<IUsbInterfaceProvider, UsbInterfaceProvider>();
        services.AddSingleton<IAdbProvider, AdbProvider>();
        services.AddSingleton<IRecoveryProvider, RecoveryProvider>();
        services.AddSingleton<IFastbootProvider, FastbootProvider>();
        services.AddSingleton<IMediaTekProvider, MediaTekProvider>();
        services.AddSingleton<IMetaModeProvider, MetaModeProvider>();
        services.AddSingleton<ISpdUnisocProvider, SpdUnisocProvider>();
        services.AddSingleton<IDiagnosticProvider, DiagnosticProvider>();
        services.AddSingleton<IProtocolCapabilityService, ProtocolCapabilityService>();
        services.AddSingleton<IDeviceDiscoveryProvider, DeviceDiscoveryProvider>();

        services.AddSingleton<IFirmwareFormatRegistry, FirmwareFormatRegistry>();
        services.AddSingleton<RomStudioService>();
        services.AddSingleton<IBootloaderManager, BootloaderManager>();
        services.AddSingleton<IFlashManager, FlashManager>();
        services.AddSingleton<IPartitionManager, PartitionManager>();

        // Register all 19 Page ViewModels + Shell
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ConnectionManagerViewModel>();
        services.AddSingleton<DeviceExplorerViewModel>();
        services.AddSingleton<AdbManagerViewModel>();
        services.AddSingleton<FastbootManagerViewModel>();
        services.AddSingleton<MediaTekToolsViewModel>();
        services.AddSingleton<MetaModeViewModel>();
        services.AddSingleton<SpdUnisocToolsViewModel>();
        services.AddSingleton<DiagDiagnosticsViewModel>();
        services.AddSingleton<BootloaderManagerViewModel>();
        services.AddSingleton<FlashManagerViewModel>();
        services.AddSingleton<FirmwareExplorerViewModel>();
        services.AddSingleton<RomStudioViewModel>();
        services.AddSingleton<PartitionManagerViewModel>();
        services.AddSingleton<DeviceProfilesViewModel>();
        services.AddSingleton<OperationHistoryViewModel>();
        services.AddSingleton<LiveLogsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DocumentationAboutViewModel>();
        services.AddSingleton<MainShellViewModel>();

        return services;
    }
}
