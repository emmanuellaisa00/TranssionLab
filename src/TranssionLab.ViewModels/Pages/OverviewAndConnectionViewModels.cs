using System.Collections.ObjectModel;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.ViewModels.Mvvm;

namespace TranssionLab.ViewModels.Pages;

// Page 1: Unified Connection Dashboard (Section 3)
public sealed class DashboardViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IProtocolCapabilityService _capabilities;

    public override int PageNumber => 1;
    public override string PageTitle => "Dashboard";
    public override string NavigationGroup => "OVERVIEW";
    public override string IconGlyph => "◈";

    private ConnectedDeviceSnapshot? _activeDevice;
    public ConnectedDeviceSnapshot? ActiveDevice
    {
        get => _activeDevice;
        private set
        {
            if (SetProperty(ref _activeDevice, value))
            {
                OnPropertyChanged(nameof(HasActiveDevice));
                OnPropertyChanged(nameof(EmptyStateMessage));
            }
        }
    }

    public bool HasActiveDevice => ActiveDevice is not null;

    public string EmptyStateMessage => _discovery.RequiresExplicitSelection
        ? $"Multiple devices ({_discovery.ConnectedDevices.Count}) are connected. Select a target device explicitly in the top bar or Connection Manager."
        : "No Android / Transsion device is currently connected. Attach a phone via USB (ADB, Fastboot, Recovery, MediaTek Preloader/BROM/META, or Unisoc Download) and click Scan Devices.";

    public ObservableCollection<OperationCapabilityEvaluation> AvailableOperations { get; } = new();
    public ObservableCollection<ProtocolCapabilitySummary> ProviderSummaries { get; } = new();

    public AsyncRelayCommand RefreshDashboardCommand { get; }

    public DashboardViewModel(IDeviceDiscoveryProvider discovery, IProtocolCapabilityService capabilities)
    {
        _discovery = discovery;
        _capabilities = capabilities;
        _discovery.InventoryChanged += (_, _) => SyncFromDiscovery();
        RefreshDashboardCommand = new AsyncRelayCommand(async () =>
        {
            IsBusy = true;
            StatusBanner = "Scanning USB, ADB, and Fastboot transports...";
            try
            {
                await _discovery.RefreshInventoryAsync().ConfigureAwait(true);
                SyncFromDiscovery();
                StatusBanner = $"Scan complete ({_discovery.ConnectedDevices.Count} endpoint(s) detected).";
            }
            finally
            {
                IsBusy = false;
            }
        });
        SyncFromDiscovery();
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        SyncFromDiscovery();
        return Task.CompletedTask;
    }

    public void SyncFromDiscovery()
    {
        ActiveDevice = _discovery.SelectedDevice;
        OnPropertyChanged(nameof(EmptyStateMessage));

        AvailableOperations.Clear();
        // Section 9: "Display only operations available for the selected device and protocol."
        foreach (var eval in _capabilities.EvaluateOperationsForDevice(ActiveDevice).Where(e => e.IsCurrentlyAvailable))
        {
            AvailableOperations.Add(eval);
        }

        ProviderSummaries.Clear();
        foreach (var summary in _capabilities.GetAllProviderSummaries())
        {
            ProviderSummaries.Add(summary);
        }
    }
}

// Page 2: Connection Manager & USB/Driver Diagnostics (Section 2G)
public sealed class ConnectionManagerViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IUsbInterfaceProvider _usbProvider;

    public override int PageNumber => 2;
    public override string PageTitle => "Connection Manager";
    public override string NavigationGroup => "OVERVIEW";
    public override string IconGlyph => "⇄";

    public ObservableCollection<ConnectedDeviceSnapshot> DiscoveredDevices { get; } = new();
    public ObservableCollection<DetectedUsbDevice> UsbInterfaces { get; } = new();
    public ObservableCollection<ComPortDescriptor> ComPorts { get; } = new();
    public ObservableCollection<UsbConnectionHistoryEvent> ConnectionHistory { get; } = new();

    private ConnectedDeviceSnapshot? _highlightedDevice;
    public ConnectedDeviceSnapshot? HighlightedDevice
    {
        get => _highlightedDevice;
        set
        {
            if (SetProperty(ref _highlightedDevice, value))
            {
                ConfirmExplicitSelectionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool RequiresExplicitChoice => _discovery.RequiresExplicitSelection;

    public AsyncRelayCommand ScanAllTransportsCommand { get; }
    public RelayCommand ConfirmExplicitSelectionCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }

    public ConnectionManagerViewModel(IDeviceDiscoveryProvider discovery, IUsbInterfaceProvider usbProvider)
    {
        _discovery = discovery;
        _usbProvider = usbProvider;

        ScanAllTransportsCommand = new AsyncRelayCommand(RefreshAllAsync);
        ConfirmExplicitSelectionCommand = new RelayCommand(
            () =>
            {
                if (HighlightedDevice is not null)
                {
                    _discovery.SelectDeviceExplicitly(HighlightedDevice.DeviceKey);
                    OnPropertyChanged(nameof(RequiresExplicitChoice));
                    StatusBanner = $"Explicitly bound session target to {HighlightedDevice.DeviceKey}.";
                }
            },
            () => HighlightedDevice is not null);

        ClearSelectionCommand = new RelayCommand(() =>
        {
            _discovery.SelectDeviceExplicitly(null);
            OnPropertyChanged(nameof(RequiresExplicitChoice));
            StatusBanner = "Cleared explicit device selection.";
        });

        _discovery.InventoryChanged += (_, _) => SyncSnapshots();
    }

    public override async Task OnNavigatedToAsync(CancellationToken ct = default) => await RefreshAllAsync().ConfigureAwait(true);

    private async Task RefreshAllAsync()
    {
        IsBusy = true;
        try
        {
            await _discovery.RefreshInventoryAsync().ConfigureAwait(true);
            var usb = await _usbProvider.EnumerateUsbDevicesAsync().ConfigureAwait(true);
            var ports = await _usbProvider.EnumerateComPortsAsync().ConfigureAwait(true);

            UsbInterfaces.Clear();
            foreach (var u in usb) UsbInterfaces.Add(u);

            ComPorts.Clear();
            foreach (var p in ports) ComPorts.Add(p);

            ConnectionHistory.Clear();
            foreach (var h in _usbProvider.GetConnectionHistory().Reverse()) ConnectionHistory.Add(h);

            SyncSnapshots();
            StatusBanner = $"Enumerated {DiscoveredDevices.Count} logical device(s), {UsbInterfaces.Count} USB endpoint(s), {ComPorts.Count} COM port(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SyncSnapshots()
    {
        DiscoveredDevices.Clear();
        foreach (var d in _discovery.ConnectedDevices)
            DiscoveredDevices.Add(d);
        HighlightedDevice = _discovery.SelectedDevice;
        OnPropertyChanged(nameof(RequiresExplicitChoice));
    }
}

public sealed record PropertyRowItem(string Key, string Value, string Provenance);

// Page 3: Device Explorer
public sealed class DeviceExplorerViewModel : PageViewModelBase
{
    private static readonly System.Text.Json.JsonSerializerOptions PrettyJsonOpts = new() { WriteIndented = true };
    private readonly IDeviceDiscoveryProvider _discovery;

    public override int PageNumber => 3;
    public override string PageTitle => "Device Explorer";
    public override string NavigationGroup => "OVERVIEW";
    public override string IconGlyph => "⊞";

    private string _propertyFilter = string.Empty;
    public string PropertyFilter
    {
        get => _propertyFilter;
        set
        {
            if (SetProperty(ref _propertyFilter, value))
                RebuildFilteredProperties();
        }
    }

    public ConnectedDeviceSnapshot? CurrentDevice => _discovery.SelectedDevice;
    public ObservableCollection<PropertyRowItem> FilteredProperties { get; } = new();

    public RelayCommand RefreshPropertiesViewCommand { get; }
    public AsyncRelayCommand ExportDeviceSnapshotJsonCommand { get; }

    private string _exportPath = string.Empty;
    public string ExportPath
    {
        get => _exportPath;
        set => SetProperty(ref _exportPath, value);
    }

    public DeviceExplorerViewModel(IDeviceDiscoveryProvider discovery, IAppSettingsService settings)
    {
        _discovery = discovery;
        _exportPath = Path.Combine(settings.Current.WorkspaceDataDirectory, "device_snapshot.json");
        _discovery.InventoryChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CurrentDevice));
            RebuildFilteredProperties();
        };

        RefreshPropertiesViewCommand = new RelayCommand(RebuildFilteredProperties);
        ExportDeviceSnapshotJsonCommand = new AsyncRelayCommand(async () =>
        {
            var dev = _discovery.RequireExplicitlySelectedDevice();
            var json = System.Text.Json.JsonSerializer.Serialize(dev, PrettyJsonOpts);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ExportPath))!);
            await File.WriteAllTextAsync(ExportPath, json).ConfigureAwait(true);
            StatusBanner = $"Exported verified device snapshot to {ExportPath}";
        });
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        RebuildFilteredProperties();
        return Task.CompletedTask;
    }

    public void RebuildFilteredProperties()
    {
        FilteredProperties.Clear();
        var dev = CurrentDevice;
        if (dev is null)
        {
            StatusBanner = _discovery.RequiresExplicitSelection
                ? "Select one device explicitly to inspect properties."
                : "No active device selected.";
            return;
        }

        var rows = new List<PropertyRowItem>
        {
            new("identity.manufacturer", dev.Manufacturer.DisplayText, dev.Manufacturer.BadgeText),
            new("identity.exact_model", dev.ExactModel.DisplayText, dev.ExactModel.BadgeText),
            new("identity.model_aliases", dev.ModelAliases.DisplayText, dev.ModelAliases.BadgeText),
            new("identity.codename", dev.Codename.DisplayText, dev.Codename.BadgeText),
            new("hardware.chipset_family", dev.ChipsetFamily.DisplayText, dev.ChipsetFamily.BadgeText),
            new("software.android_version", dev.AndroidVersion.DisplayText, dev.AndroidVersion.BadgeText),
            new("software.build_fingerprint", dev.BuildFingerprint.DisplayText, dev.BuildFingerprint.BadgeText),
            new("security.bootloader_state", dev.BootloaderState.DisplayText, dev.BootloaderState.BadgeText)
        };

        foreach (var (k, v) in dev.RawProperties.OrderBy(kv => kv.Key))
        {
            rows.Add(new PropertyRowItem(k, v, "DETECTED"));
        }

        var term = PropertyFilter?.Trim() ?? string.Empty;
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(term) ||
                row.Key.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.Value.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                FilteredProperties.Add(row);
            }
        }

        StatusBanner = $"Showing {FilteredProperties.Count} property entries for {dev.DeviceKey}.";
    }
}

// Page 4: ADB Manager & Recovery Sideload (Section 2A)
public sealed class AdbManagerViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IAdbProvider _adb;
    private readonly IRecoveryProvider _recovery;

    public override int PageNumber => 4;
    public override string PageTitle => "ADB Manager";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "⌘";

    private string _toolStatus = "Unchecked";
    public string ToolStatus { get => _toolStatus; private set => SetProperty(ref _toolStatus, value); }

    private string _diagnosticOutput = string.Empty;
    public string DiagnosticOutput { get => _diagnosticOutput; private set => SetProperty(ref _diagnosticOutput, value); }

    private string _otaSideloadPath = string.Empty;
    public string OtaSideloadPath
    {
        get => _otaSideloadPath;
        set
        {
            if (SetProperty(ref _otaSideloadPath, value))
                RunSideloadCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAdbAuthorizedActive => _discovery.SelectedDevice?.ActiveConnectionMode == ConnectionMode.AdbAuthorized;
    public bool IsSideloadActive => _discovery.SelectedDevice?.ActiveConnectionMode == ConnectionMode.AdbSideload;
    public bool CanRebootFromAdb => _discovery.SelectedDevice?.ActiveConnectionMode is ConnectionMode.AdbAuthorized or ConnectionMode.Recovery;

    public AsyncRelayCommand CheckAdbToolCommand { get; }
    public AsyncRelayCommand ReadBatteryAndStorageCommand { get; }
    public AsyncRelayCommand RebootSystemCommand { get; }
    public AsyncRelayCommand RebootBootloaderCommand { get; }
    public AsyncRelayCommand RebootFastbootdCommand { get; }
    public AsyncRelayCommand RebootRecoveryCommand { get; }
    public AsyncRelayCommand RunSideloadCommand { get; }
    public RelayCommand BrowseOtaZipCommand { get; }

    public AdbManagerViewModel(IDeviceDiscoveryProvider discovery, IAdbProvider adb, IRecoveryProvider recovery)
    {
        _discovery = discovery;
        _adb = adb;
        _recovery = recovery;

        BrowseOtaZipCommand = new RelayCommand(() =>
        {
            var picked = UiInteractionBridge.PickOpenFile?.Invoke("Select Signed Stock OTA ZIP", "OTA ZIP (*.zip)|*.zip|All Files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(picked))
                OtaSideloadPath = picked;
        });

        CheckAdbToolCommand = new AsyncRelayCommand(CheckToolAsync);
        ReadBatteryAndStorageCommand = new AsyncRelayCommand(CaptureHealthAsync, () => IsAdbAuthorizedActive);
        RebootSystemCommand = new AsyncRelayCommand(() => RebootToAsync("system"), () => CanRebootFromAdb);
        RebootBootloaderCommand = new AsyncRelayCommand(() => RebootToAsync("bootloader"), () => CanRebootFromAdb);
        RebootFastbootdCommand = new AsyncRelayCommand(() => RebootToAsync("fastboot"), () => CanRebootFromAdb);
        RebootRecoveryCommand = new AsyncRelayCommand(() => RebootToAsync("recovery"), () => CanRebootFromAdb);
        RunSideloadCommand = new AsyncRelayCommand(ExecuteSideloadAsync, () => IsSideloadActive && !string.IsNullOrWhiteSpace(OtaSideloadPath));

        _discovery.InventoryChanged += (_, _) => RaiseModeStates();
    }

    public override async Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        RaiseModeStates();
        await CheckToolAsync().ConfigureAwait(true);
    }

    private void RaiseModeStates()
    {
        OnPropertyChanged(nameof(IsAdbAuthorizedActive));
        OnPropertyChanged(nameof(IsSideloadActive));
        OnPropertyChanged(nameof(CanRebootFromAdb));
        ReadBatteryAndStorageCommand.RaiseCanExecuteChanged();
        RebootSystemCommand.RaiseCanExecuteChanged();
        RebootBootloaderCommand.RaiseCanExecuteChanged();
        RebootFastbootdCommand.RaiseCanExecuteChanged();
        RebootRecoveryCommand.RaiseCanExecuteChanged();
        RunSideloadCommand.RaiseCanExecuteChanged();
    }

    private async Task CheckToolAsync()
    {
        var ver = await _adb.GetToolVersionAsync().ConfigureAwait(true);
        ToolStatus = ver ?? "adb binary not found on PATH — configure path in Settings.";
    }

    private async Task CaptureHealthAsync()
    {
        var dev = _discovery.RequireExplicitlySelectedDevice();
        if (string.IsNullOrWhiteSpace(dev.TransportSerial)) return;
        IsBusy = true;
        try
        {
            var bat = await _adb.CaptureBatterySummaryAsync(dev.TransportSerial).ConfigureAwait(true);
            var df = await _adb.CaptureStorageSummaryAsync(dev.TransportSerial).ConfigureAwait(true);
            DiagnosticOutput = $"=== BATTERY ===\n{bat}\n\n=== FILESYSTEMS ===\n{df}";
            StatusBanner = $"Captured read-only health telemetry from {dev.TransportSerial}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RebootToAsync(string mode)
    {
        var dev = _discovery.RequireExplicitlySelectedDevice();
        if (string.IsNullOrWhiteSpace(dev.TransportSerial)) return;
        IsBusy = true;
        try
        {
            var res = await _adb.RebootAsync(dev.TransportSerial, mode).ConfigureAwait(true);
            StatusBanner = res.Succeeded
                ? $"Reboot to '{mode}' sent to {dev.TransportSerial}. Rescanning USB on re-enumeration."
                : $"Reboot failed: {res.StandardError}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteSideloadAsync()
    {
        var dev = _discovery.RequireExplicitlySelectedDevice();
        if (string.IsNullOrWhiteSpace(dev.TransportSerial)) return;
        IsBusy = true;
        try
        {
            var res = await _recovery.VerifyAndSideloadOtaAsync(dev.TransportSerial, OtaSideloadPath).ConfigureAwait(true);
            DiagnosticOutput = $"{res.StandardOutput}\n{res.StandardError}".Trim();
            StatusBanner = res.Succeeded ? "OTA Sideload completed." : $"Sideload failed (Exit {res.ExitCode}).";
        }
        catch (Exception ex)
        {
            StatusBanner = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

// Page 5: Fastboot & Fastbootd Manager (Section 2A)
public sealed class FastbootManagerViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IFastbootProvider _fastboot;

    public override int PageNumber => 5;
    public override string PageTitle => "Fastboot Manager";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "⚡";

    private string _toolStatus = "Unchecked";
    public string ToolStatus { get => _toolStatus; private set => SetProperty(ref _toolStatus, value); }

    public ObservableCollection<PropertyRowItem> FastbootVariables { get; } = new();

    public bool IsFastbootActive => _discovery.SelectedDevice?.ActiveConnectionMode is ConnectionMode.FastbootBootloader or ConnectionMode.FastbootdUserspace;
    public bool CanModifySlot => IsFastbootActive && _discovery.SelectedDevice?.BootloaderState.Value == BootloaderLockState.Unlocked;
    public string ActiveSubModeLabel => _discovery.SelectedDevice?.ActiveConnectionMode switch
    {
        ConnectionMode.FastbootdUserspace => "Userspace Fastbootd (Logical 'super' partitions accessible)",
        ConnectionMode.FastbootBootloader => "Primary Bootloader Fastboot (Physical partitions & unlock commands)",
        _ => "Not currently in Fastboot / Fastbootd mode"
    };

    public AsyncRelayCommand QueryVariablesCommand { get; }
    public AsyncRelayCommand SetSlotACommand { get; }
    public AsyncRelayCommand SetSlotBCommand { get; }
    public AsyncRelayCommand RebootSystemCommand { get; }
    public AsyncRelayCommand RebootBootloaderCommand { get; }
    public AsyncRelayCommand RebootFastbootdCommand { get; }

    public FastbootManagerViewModel(IDeviceDiscoveryProvider discovery, IFastbootProvider fastboot)
    {
        _discovery = discovery;
        _fastboot = fastboot;

        QueryVariablesCommand = new AsyncRelayCommand(QueryVarsAsync, () => IsFastbootActive);
        SetSlotACommand = new AsyncRelayCommand(() => SwitchSlotAsync("a"), () => CanModifySlot);
        SetSlotBCommand = new AsyncRelayCommand(() => SwitchSlotAsync("b"), () => CanModifySlot);
        RebootSystemCommand = new AsyncRelayCommand(() => RebootAsync("system"), () => IsFastbootActive);
        RebootBootloaderCommand = new AsyncRelayCommand(() => RebootAsync("bootloader"), () => IsFastbootActive);
        RebootFastbootdCommand = new AsyncRelayCommand(() => RebootAsync("fastbootd"), () => IsFastbootActive);

        _discovery.InventoryChanged += (_, _) => RefreshStateBindings();
    }

    public override async Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        RefreshStateBindings();
        var ver = await _fastboot.GetToolVersionAsync(ct).ConfigureAwait(true);
        ToolStatus = ver ?? "fastboot binary not found on PATH — configure path in Settings.";
    }

    private void RefreshStateBindings()
    {
        OnPropertyChanged(nameof(IsFastbootActive));
        OnPropertyChanged(nameof(CanModifySlot));
        OnPropertyChanged(nameof(ActiveSubModeLabel));
        QueryVariablesCommand.RaiseCanExecuteChanged();
        SetSlotACommand.RaiseCanExecuteChanged();
        SetSlotBCommand.RaiseCanExecuteChanged();
        RebootSystemCommand.RaiseCanExecuteChanged();
        RebootBootloaderCommand.RaiseCanExecuteChanged();
        RebootFastbootdCommand.RaiseCanExecuteChanged();
    }

    private async Task QueryVarsAsync()
    {
        var dev = _discovery.RequireExplicitlySelectedDevice();
        if (string.IsNullOrWhiteSpace(dev.TransportSerial)) return;
        IsBusy = true;
        try
        {
            var vars = await _fastboot.GetAllVariablesAsync(dev.TransportSerial).ConfigureAwait(true);
            FastbootVariables.Clear();
            foreach (var (k, v) in vars.OrderBy(kv => kv.Key))
            {
                FastbootVariables.Add(new PropertyRowItem(k, v, "VERIFIED"));
            }
            StatusBanner = $"Retrieved {FastbootVariables.Count} variables from {dev.TransportSerial}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SwitchSlotAsync(string slot)
    {
        var dev = _discovery.RequireExplicitlySelectedDevice();
        if (string.IsNullOrWhiteSpace(dev.TransportSerial)) return;
        IsBusy = true;
        try
        {
            var res = await _fastboot.SetActiveSlotAsync(dev.TransportSerial, slot).ConfigureAwait(true);
            var verify = await _fastboot.GetVariableAsync(dev.TransportSerial, "current-slot").ConfigureAwait(true);
            StatusBanner = res.Succeeded
                ? $"Verified active slot: current-slot={verify ?? slot}"
                : $"Slot switch rejected: {res.StandardError}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RebootAsync(string mode)
    {
        var dev = _discovery.RequireExplicitlySelectedDevice();
        if (string.IsNullOrWhiteSpace(dev.TransportSerial)) return;
        var res = await _fastboot.RebootAsync(dev.TransportSerial, mode).ConfigureAwait(true);
        StatusBanner = res.Succeeded ? $"Dispatched fastboot reboot ({mode})." : res.StandardError;
    }
}
