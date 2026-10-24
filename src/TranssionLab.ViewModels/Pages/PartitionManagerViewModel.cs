using System.Collections.ObjectModel;
using System.Globalization;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.ViewModels.Mvvm;

namespace TranssionLab.ViewModels.Pages;

/// <summary>
/// Page 13: Dedicated Partition Manager (GPT, Dynamic Super Logical Partitions,
/// Controlled Erase, Authorized Readback, and Unlocked VBMeta Policy Control).
/// </summary>
public sealed class PartitionManagerViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IPartitionManager _partitionManager;
    private readonly FlashManagerViewModel _flashManagerVm;
    private readonly IAppSettingsService _settings;

    public override int PageNumber => 13;
    public override string PageTitle => "Partition Manager";
    public override string NavigationGroup => "SERVICING & FIRMWARE";
    public override string IconGlyph => "⛁";

    private DevicePartitionTableSnapshot? _currentSnapshot;
    public DevicePartitionTableSnapshot? CurrentSnapshot
    {
        get => _currentSnapshot;
        private set
        {
            if (SetProperty(ref _currentSnapshot, value))
            {
                OnPropertyChanged(nameof(HasPartitionTable));
                ExportTableJsonCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasPartitionTable => CurrentSnapshot is not null;

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
                ApplyFilter();
        }
    }

    private bool _showLogicalOnly;
    public bool ShowLogicalOnly
    {
        get => _showLogicalOnly;
        set
        {
            if (SetProperty(ref _showLogicalOnly, value))
                ApplyFilter();
        }
    }

    public ObservableCollection<DevicePartitionEntry> FilteredPartitions { get; } = new();

    private DevicePartitionEntry? _selectedPartition;
    public DevicePartitionEntry? SelectedPartition
    {
        get => _selectedPartition;
        set
        {
            if (SetProperty(ref _selectedPartition, value))
            {
                if (value is not null)
                {
                    if (value.IsLogicalInSuper)
                    {
                        LogicalTargetName = value.PartitionName;
                        if (value.SizeBytes is > 0)
                            LogicalTargetSizeBytesText = value.SizeBytes.Value.ToString(CultureInfo.InvariantCulture);
                    }
                    if (value.CanEraseInCurrentMode)
                    {
                        EraseTargetName = value.PartitionName;
                    }
                    if (value.PartitionName.StartsWith("vbmeta", StringComparison.OrdinalIgnoreCase))
                    {
                        VbmetaPartitionName = value.PartitionName;
                    }
                }
                RaiseActionCommands();
            }
        }
    }

    private string _readbackOutputDirectory;
    public string ReadbackOutputDirectory
    {
        get => _readbackOutputDirectory;
        set => SetProperty(ref _readbackOutputDirectory, value);
    }

    // Dynamic Super Logical Partition Controls
    private string _logicalTargetName = "product_a";
    public string LogicalTargetName
    {
        get => _logicalTargetName;
        set
        {
            if (SetProperty(ref _logicalTargetName, value))
                RaiseActionCommands();
        }
    }

    private string _logicalTargetSizeBytesText = "2147483648";
    public string LogicalTargetSizeBytesText
    {
        get => _logicalTargetSizeBytesText;
        set
        {
            if (SetProperty(ref _logicalTargetSizeBytesText, value))
                RaiseActionCommands();
        }
    }

    private string _superConfirmationText = string.Empty;
    public string SuperConfirmationText
    {
        get => _superConfirmationText;
        set
        {
            if (SetProperty(ref _superConfirmationText, value))
                RaiseActionCommands();
        }
    }

    // Controlled Erase Controls
    private string _eraseTargetName = "metadata";
    public string EraseTargetName
    {
        get => _eraseTargetName;
        set
        {
            if (SetProperty(ref _eraseTargetName, value))
            {
                OnPropertyChanged(nameof(ExpectedErasePhrase));
                RaiseActionCommands();
            }
        }
    }

    private string _eraseConfirmationText = string.Empty;
    public string EraseConfirmationText
    {
        get => _eraseConfirmationText;
        set
        {
            if (SetProperty(ref _eraseConfirmationText, value))
                RaiseActionCommands();
        }
    }

    // Unlocked VBMeta Controls
    private string _vbmetaPartitionName = "vbmeta_a";
    public string VbmetaPartitionName
    {
        get => _vbmetaPartitionName;
        set
        {
            if (SetProperty(ref _vbmetaPartitionName, value))
                RaiseActionCommands();
        }
    }

    private string _vbmetaImagePath = string.Empty;
    public string VbmetaImagePath
    {
        get => _vbmetaImagePath;
        set
        {
            if (SetProperty(ref _vbmetaImagePath, value))
                RaiseActionCommands();
        }
    }

    private bool _disableVerity = true;
    public bool DisableVerity { get => _disableVerity; set => SetProperty(ref _disableVerity, value); }

    private bool _disableVerification = true;
    public bool DisableVerification { get => _disableVerification; set => SetProperty(ref _disableVerification, value); }

    private string _vbmetaConfirmationText = string.Empty;
    public string VbmetaConfirmationText
    {
        get => _vbmetaConfirmationText;
        set
        {
            if (SetProperty(ref _vbmetaConfirmationText, value))
                RaiseActionCommands();
        }
    }

    private PartitionModificationResult? _lastModificationResult;
    public PartitionModificationResult? LastModificationResult
    {
        get => _lastModificationResult;
        private set => SetProperty(ref _lastModificationResult, value);
    }

    public string ExpectedSuperPhrase => _discovery.SelectedDevice?.TransportSerial is { Length: > 0 } s
        ? $"SUPER {s}"
        : "SUPER <SERIAL>";

    public string ExpectedErasePhrase => _discovery.SelectedDevice?.TransportSerial is { Length: > 0 } s
        ? $"ERASE {EraseTargetName.Trim()} {s}"
        : $"ERASE {EraseTargetName.Trim()} <SERIAL>";

    public string ExpectedVbmetaPhrase => _discovery.SelectedDevice?.TransportSerial is { Length: > 0 } s
        ? $"VBMETA {s}"
        : "VBMETA <SERIAL>";

    public bool IsFastbootdUnlocked =>
        _discovery.SelectedDevice?.ActiveConnectionMode == ConnectionMode.FastbootdUserspace &&
        _discovery.SelectedDevice?.BootloaderState.Value == BootloaderLockState.Unlocked;

    public bool IsFastbootAnyUnlocked =>
        _discovery.SelectedDevice?.ActiveConnectionMode is ConnectionMode.FastbootBootloader or ConnectionMode.FastbootdUserspace &&
        _discovery.SelectedDevice?.BootloaderState.Value == BootloaderLockState.Unlocked;

    public AsyncRelayCommand ScanPartitionsCommand { get; }
    public AsyncRelayCommand ExportTableJsonCommand { get; }
    public AsyncRelayCommand ReadbackSelectedPartitionCommand { get; }
    public RelayCommand StageSelectedInFlashManagerCommand { get; }
    public RelayCommand BrowseReadbackDirCommand { get; }
    public RelayCommand BrowseVbmetaImageCommand { get; }
    public AsyncRelayCommand CreateLogicalCommand { get; }
    public AsyncRelayCommand ResizeLogicalCommand { get; }
    public AsyncRelayCommand DeleteLogicalCommand { get; }
    public AsyncRelayCommand ErasePartitionCommand { get; }
    public AsyncRelayCommand FlashVbmetaCommand { get; }

    public PartitionManagerViewModel(
        IDeviceDiscoveryProvider discovery,
        IPartitionManager partitionManager,
        FlashManagerViewModel flashManagerVm,
        IAppSettingsService settings)
    {
        _discovery = discovery;
        _partitionManager = partitionManager;
        _flashManagerVm = flashManagerVm;
        _settings = settings;
        _readbackOutputDirectory = Path.Combine(settings.Current.WorkspaceDataDirectory, "partition_backups");

        ScanPartitionsCommand = new AsyncRelayCommand(ScanAsync, CanScanCurrentDevice);
        ExportTableJsonCommand = new AsyncRelayCommand(ExportTableAsync, () => CurrentSnapshot is not null);
        ReadbackSelectedPartitionCommand = new AsyncRelayCommand(
            ReadbackSelectedAsync,
            () => SelectedPartition is { CanReadbackInCurrentMode: true });

        StageSelectedInFlashManagerCommand = new RelayCommand(
            () =>
            {
                if (SelectedPartition is null) return;
                _flashManagerVm.NewPartitionName = SelectedPartition.PartitionName;
                StatusBanner = $"Staged target partition '{SelectedPartition.PartitionName}' in Flash Manager (#14). Select an image file there to build plan.";
            },
            () => SelectedPartition is not null &&
                  SelectedPartition.AccessPolicy is PartitionAccessPolicy.SafeFlashable or PartitionAccessPolicy.LogicalSuperRequiresFastbootd);

        BrowseReadbackDirCommand = new RelayCommand(() =>
        {
            var dir = UiInteractionBridge.PickDirectory?.Invoke("Select Partition Backup Output Folder");
            if (!string.IsNullOrWhiteSpace(dir))
                ReadbackOutputDirectory = dir;
        });

        BrowseVbmetaImageCommand = new RelayCommand(() =>
        {
            var file = UiInteractionBridge.PickOpenFile?.Invoke("Select Verified AVB0 vbmeta.img", "VBMeta Image (*.img)|*.img|All Files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(file))
                VbmetaImagePath = file;
        });

        CreateLogicalCommand = new AsyncRelayCommand(
            () => MutateLogicalAsync("create"),
            () => IsFastbootdUnlocked &&
                  !string.IsNullOrWhiteSpace(LogicalTargetName) &&
                  long.TryParse(LogicalTargetSizeBytesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sz) && sz >= 4096 &&
                  string.Equals(SuperConfirmationText?.Trim(), ExpectedSuperPhrase, StringComparison.Ordinal));

        ResizeLogicalCommand = new AsyncRelayCommand(
            () => MutateLogicalAsync("resize"),
            () => IsFastbootdUnlocked &&
                  !string.IsNullOrWhiteSpace(LogicalTargetName) &&
                  long.TryParse(LogicalTargetSizeBytesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sz) && sz >= 4096 &&
                  string.Equals(SuperConfirmationText?.Trim(), ExpectedSuperPhrase, StringComparison.Ordinal));

        DeleteLogicalCommand = new AsyncRelayCommand(
            () => MutateLogicalAsync("delete"),
            () => IsFastbootdUnlocked &&
                  !string.IsNullOrWhiteSpace(LogicalTargetName) &&
                  string.Equals(SuperConfirmationText?.Trim(), ExpectedSuperPhrase, StringComparison.Ordinal));

        ErasePartitionCommand = new AsyncRelayCommand(
            ExecuteSafeEraseAsync,
            () => IsFastbootAnyUnlocked &&
                  !string.IsNullOrWhiteSpace(EraseTargetName) &&
                  string.Equals(EraseConfirmationText?.Trim(), ExpectedErasePhrase, StringComparison.Ordinal));

        FlashVbmetaCommand = new AsyncRelayCommand(
            ExecuteFlashVbmetaAsync,
            () => IsFastbootAnyUnlocked &&
                  !string.IsNullOrWhiteSpace(VbmetaPartitionName) &&
                  !string.IsNullOrWhiteSpace(VbmetaImagePath) &&
                  string.Equals(VbmetaConfirmationText?.Trim(), ExpectedVbmetaPhrase, StringComparison.Ordinal));

        _discovery.InventoryChanged += (_, _) => RaiseDeviceBindings();
        StatusBanner = "Connect in Fastboot, Fastbootd, Authorized ADB, or Recovery to inspect live GPT & Dynamic Super partitions.";
    }

    public override Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        RaiseDeviceBindings();
        return Task.CompletedTask;
    }

    private bool CanScanCurrentDevice() =>
        _discovery.SelectedDevice?.ActiveConnectionMode is
            ConnectionMode.FastbootBootloader or
            ConnectionMode.FastbootdUserspace or
            ConnectionMode.AdbAuthorized or
            ConnectionMode.Recovery;

    private void RaiseDeviceBindings()
    {
        OnPropertyChanged(nameof(ExpectedSuperPhrase));
        OnPropertyChanged(nameof(ExpectedErasePhrase));
        OnPropertyChanged(nameof(ExpectedVbmetaPhrase));
        OnPropertyChanged(nameof(IsFastbootdUnlocked));
        OnPropertyChanged(nameof(IsFastbootAnyUnlocked));
        ScanPartitionsCommand.RaiseCanExecuteChanged();
        RaiseActionCommands();
    }

    private void RaiseActionCommands()
    {
        ReadbackSelectedPartitionCommand.RaiseCanExecuteChanged();
        StageSelectedInFlashManagerCommand.RaiseCanExecuteChanged();
        CreateLogicalCommand.RaiseCanExecuteChanged();
        ResizeLogicalCommand.RaiseCanExecuteChanged();
        DeleteLogicalCommand.RaiseCanExecuteChanged();
        ErasePartitionCommand.RaiseCanExecuteChanged();
        FlashVbmetaCommand.RaiseCanExecuteChanged();
    }

    private async Task ScanAsync()
    {
        IsBusy = true;
        try
        {
            var dev = _discovery.RequireExplicitlySelectedDevice();
            CurrentSnapshot = await _partitionManager.ScanDevicePartitionsAsync(dev).ConfigureAwait(true);
            ApplyFilter();
            StatusBanner = $"Enumerated {CurrentSnapshot.TotalPartitions} partition(s) ({CurrentSnapshot.LogicalPartitionsCount} logical super, {CurrentSnapshot.ProtectedPartitionsCount} protected/blocked) on {CurrentSnapshot.TransportSerial}.";
        }
        catch (Exception ex)
        {
            StatusBanner = $"Partition scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ApplyFilter()
    {
        FilteredPartitions.Clear();
        if (CurrentSnapshot is null)
            return;

        var term = FilterText?.Trim() ?? string.Empty;
        foreach (var p in CurrentSnapshot.Partitions)
        {
            if (ShowLogicalOnly && !p.IsLogicalInSuper)
                continue;

            if (!string.IsNullOrEmpty(term) &&
                !p.PartitionName.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !p.AccessPolicy.ToString().Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !(p.DeviceBlockNode?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            FilteredPartitions.Add(p);
        }
    }

    private async Task ExportTableAsync()
    {
        if (CurrentSnapshot is null) return;
        var defaultPath = Path.Combine(
            _settings.Current.WorkspaceDataDirectory,
            "exports",
            $"partitions-{CurrentSnapshot.TransportSerial}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");

        var chosen = UiInteractionBridge.PickSaveFile?.Invoke(
            "Export Device Partition Table Snapshot",
            "JSON Files (*.json)|*.json",
            Path.GetFileName(defaultPath)) ?? defaultPath;

        var saved = await _partitionManager.ExportPartitionTableJsonAsync(CurrentSnapshot, chosen).ConfigureAwait(true);
        StatusBanner = $"Exported partition table JSON to '{saved}'.";
    }

    private async Task ReadbackSelectedAsync()
    {
        if (SelectedPartition is null) return;
        IsBusy = true;
        try
        {
            var dev = _discovery.RequireExplicitlySelectedDevice();
            LastModificationResult = await _partitionManager.BackupPartitionViaAdbAsync(
                dev,
                SelectedPartition,
                ReadbackOutputDirectory).ConfigureAwait(true);
            StatusBanner = LastModificationResult.Summary;
        }
        catch (Exception ex)
        {
            StatusBanner = $"Readback blocked/failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task MutateLogicalAsync(string kind)
    {
        IsBusy = true;
        try
        {
            var dev = _discovery.RequireExplicitlySelectedDevice();
            long.TryParse(LogicalTargetSizeBytesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes);

            LastModificationResult = kind switch
            {
                "create" => await _partitionManager.CreateLogicalPartitionAsync(dev, LogicalTargetName, bytes, SuperConfirmationText).ConfigureAwait(true),
                "resize" => await _partitionManager.ResizeLogicalPartitionAsync(dev, LogicalTargetName, bytes, SuperConfirmationText).ConfigureAwait(true),
                _ => await _partitionManager.DeleteLogicalPartitionAsync(dev, LogicalTargetName, SuperConfirmationText).ConfigureAwait(true)
            };

            SuperConfirmationText = string.Empty;
            StatusBanner = LastModificationResult.Summary;
            CurrentSnapshot = await _partitionManager.ScanDevicePartitionsAsync(dev).ConfigureAwait(true);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusBanner = $"Super logical partition operation failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteSafeEraseAsync()
    {
        IsBusy = true;
        try
        {
            var dev = _discovery.RequireExplicitlySelectedDevice();
            LastModificationResult = await _partitionManager.ErasePartitionAsync(
                dev,
                EraseTargetName,
                EraseConfirmationText).ConfigureAwait(true);
            EraseConfirmationText = string.Empty;
            StatusBanner = LastModificationResult.Summary;
        }
        catch (Exception ex)
        {
            StatusBanner = $"Erase blocked/failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteFlashVbmetaAsync()
    {
        IsBusy = true;
        try
        {
            var dev = _discovery.RequireExplicitlySelectedDevice();
            LastModificationResult = await _partitionManager.FlashVbmetaWithPolicyAsync(
                dev,
                VbmetaPartitionName,
                VbmetaImagePath,
                DisableVerity,
                DisableVerification,
                VbmetaConfirmationText).ConfigureAwait(true);
            VbmetaConfirmationText = string.Empty;
            StatusBanner = LastModificationResult.Summary;
        }
        catch (Exception ex)
        {
            StatusBanner = $"VBMeta flash blocked/failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
