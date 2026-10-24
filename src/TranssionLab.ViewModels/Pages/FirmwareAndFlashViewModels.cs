using System.Collections.ObjectModel;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Firmware;
using TranssionLab.ViewModels.Mvvm;

namespace TranssionLab.ViewModels.Pages;

// Page 11: Firmware Explorer (Section 6)
public sealed class FirmwareExplorerViewModel : PageViewModelBase
{
    private readonly IFirmwareFormatRegistry _registry;

    public override int PageNumber => 11;
    public override string PageTitle => "Firmware Explorer";
    public override string NavigationGroup => "SERVICING & FIRMWARE";
    public override string IconGlyph => "📦";

    private string _selectedFilePath = string.Empty;
    public string SelectedFilePath
    {
        get => _selectedFilePath;
        set
        {
            if (SetProperty(ref _selectedFilePath, value))
                InspectFirmwareCommand.RaiseCanExecuteChanged();
        }
    }

    private FirmwareInspectionResult? _inspectionResult;
    public FirmwareInspectionResult? InspectionResult
    {
        get => _inspectionResult;
        private set
        {
            if (SetProperty(ref _inspectionResult, value))
                OnPropertyChanged(nameof(HasInspectionResult));
        }
    }

    public bool HasInspectionResult => InspectionResult is not null;

    public ObservableCollection<PartitionImageMetadata> Partitions { get; } = new();
    public ObservableCollection<FirmwareArchiveEntry> ArchiveEntries { get; } = new();
    public ObservableCollection<string> WarningsAndNotes { get; } = new();

    public AsyncRelayCommand InspectFirmwareCommand { get; }
    public RelayCommand BrowseFirmwareFileCommand { get; }

    public FirmwareExplorerViewModel(IFirmwareFormatRegistry registry)
    {
        _registry = registry;
        InspectFirmwareCommand = new AsyncRelayCommand(
            InspectAsync,
            () => !string.IsNullOrWhiteSpace(SelectedFilePath));
        BrowseFirmwareFileCommand = new RelayCommand(() =>
        {
            var picked = UiInteractionBridge.PickOpenFile?.Invoke(
                "Select Firmware Package or Partition Image",
                "Supported Firmware (*.zip;*.tar;*.gz;*.tgz;*.bin;*.pac;*.txt;*.xml;*.img)|*.zip;*.tar;*.gz;*.tgz;*.bin;*.pac;*.txt;*.xml;*.img|All Files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(picked))
                SelectedFilePath = picked;
        });
        StatusBanner = $"Supported formats: {string.Join(", ", _registry.SupportedFormats)}";
    }

    private async Task InspectAsync()
    {
        IsBusy = true;
        try
        {
            var res = await _registry.InspectAsync(SelectedFilePath).ConfigureAwait(true);
            InspectionResult = res;

            Partitions.Clear();
            foreach (var p in res.Partitions) Partitions.Add(p);

            ArchiveEntries.Clear();
            foreach (var a in res.ArchiveEntries) ArchiveEntries.Add(a);

            WarningsAndNotes.Clear();
            foreach (var w in res.ParsingWarnings) WarningsAndNotes.Add($"[Warning] {w}");
            foreach (var d in res.MissingDependencies) WarningsAndNotes.Add($"[Dependency] {d}");

            StatusBanner = $"Inspected '{res.FileName}': Format={res.PackageFormat}, Partitions={res.Partitions.Count}, SHA256={(res.Sha256Hex is { Length: >= 16 } s ? s[..16] + "..." : res.Sha256Hex)}";
        }
        catch (Exception ex)
        {
            StatusBanner = $"Inspection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

// Page 12: ROM Studio (Section 6)
public sealed class RomStudioViewModel : PageViewModelBase
{
    private readonly RomStudioService _romStudio;
    private readonly IFirmwareFormatRegistry _firmwareRegistry;

    public override int PageNumber => 12;
    public override string PageTitle => "ROM Studio";
    public override string NavigationGroup => "SERVICING & FIRMWARE";
    public override string IconGlyph => "🛠";

    private string _stockArchivePath = string.Empty;
    public string StockArchivePath
    {
        get => _stockArchivePath;
        set
        {
            if (SetProperty(ref _stockArchivePath, value))
                ExtractEntryCommand.RaiseCanExecuteChanged();
        }
    }

    private string _entryToExtract = "boot.img";
    public string EntryToExtract
    {
        get => _entryToExtract;
        set
        {
            if (SetProperty(ref _entryToExtract, value))
                ExtractEntryCommand.RaiseCanExecuteChanged();
        }
    }

    private string _stagingOutputDirectory;
    public string StagingOutputDirectory
    {
        get => _stagingOutputDirectory;
        set => SetProperty(ref _stagingOutputDirectory, value);
    }

    private string _aospWorkspacePath = string.Empty;
    public string AospWorkspacePath
    {
        get => _aospWorkspacePath;
        set => SetProperty(ref _aospWorkspacePath, value);
    }

    private RomBuildEnvironmentReport? _buildEnvReport;
    public RomBuildEnvironmentReport? BuildEnvReport
    {
        get => _buildEnvReport;
        private set => SetProperty(ref _buildEnvReport, value);
    }

    public ObservableCollection<string> ExtractedFiles { get; } = new();

    public AsyncRelayCommand ExtractEntryCommand { get; }
    public RelayCommand ValidateSourceBuildEnvCommand { get; }
    public RelayCommand BrowseStockArchiveCommand { get; }
    public RelayCommand BrowseAospWorkspaceCommand { get; }

    public RomStudioViewModel(RomStudioService romStudio, IFirmwareFormatRegistry firmwareRegistry, IAppSettingsService settings)
    {
        _romStudio = romStudio;
        _firmwareRegistry = firmwareRegistry;
        _stagingOutputDirectory = Path.Combine(settings.Current.WorkspaceDataDirectory, "staging_images");

        BrowseStockArchiveCommand = new RelayCommand(() =>
        {
            var picked = UiInteractionBridge.PickOpenFile?.Invoke(
                "Select Stock Firmware Archive (.zip / .tar / .pac)",
                "Firmware Containers (*.zip;*.tar;*.gz;*.pac)|*.zip;*.tar;*.gz;*.pac|All Files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(picked))
                StockArchivePath = picked;
        });

        BrowseAospWorkspaceCommand = new RelayCommand(() =>
        {
            var dir = UiInteractionBridge.PickDirectory?.Invoke("Select AOSP / LineageOS Source Workspace");
            if (!string.IsNullOrWhiteSpace(dir))
                AospWorkspacePath = dir;
        });

        ExtractEntryCommand = new AsyncRelayCommand(
            async () =>
            {
                IsBusy = true;
                try
                {
                    var extracted = await _romStudio.ExtractStockPartitionsForFastbootAsync(
                        StockArchivePath, [EntryToExtract], StagingOutputDirectory).ConfigureAwait(true);
                    foreach (var f in extracted) ExtractedFiles.Add(f);
                    StatusBanner = extracted.Count > 0
                        ? $"Extracted to '{extracted[0]}'."
                        : "No unprotected entry extracted.";
                }
                catch (Exception ex)
                {
                    StatusBanner = $"Extraction error: {ex.Message}";
                }
                finally
                {
                    IsBusy = false;
                }
            },
            () => !string.IsNullOrWhiteSpace(StockArchivePath) && !string.IsNullOrWhiteSpace(EntryToExtract));

        ValidateSourceBuildEnvCommand = new RelayCommand(() =>
        {
            BuildEnvReport = _romStudio.ValidateCustomRomSourceWorkspace(AospWorkspacePath);
            StatusBanner = BuildEnvReport.ReadyForSourceBuild
                ? "Workspace satisfies AOSP/LineageOS custom ROM source build prerequisites."
                : $"Source build workspace incomplete ({BuildEnvReport.ValidationFindings.Count} requirement finding(s)).";
        });

        StatusBanner = "Stock Extraction prepares verified partition images; Custom ROM builds require full AOSP source, device trees, kernel, and >=350 GiB Linux/WSL2 storage.";
    }
}

public sealed class FlashQueueRow : ObservableObject
{
    public required string PartitionName { get; init; }
    public required string ImageFilePath { get; init; }
}

// Page 13: Safe 11-Step Flash Manager (Section 7)
public sealed class FlashManagerViewModel : PageViewModelBase, IDisposable
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IFlashManager _flashManager;
    private CancellationTokenSource? _flashCts;

    public override int PageNumber => 13;
    public override string PageTitle => "Flash Manager";
    public override string NavigationGroup => "SERVICING & FIRMWARE";
    public override string IconGlyph => "🔥";

    private string _newPartitionName = "boot_a";
    public string NewPartitionName { get => _newPartitionName; set => SetProperty(ref _newPartitionName, value); }

    private string _newImageFilePath = string.Empty;
    public string NewImageFilePath
    {
        get => _newImageFilePath;
        set
        {
            if (SetProperty(ref _newImageFilePath, value))
                AddToQueueCommand.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<FlashQueueRow> FlashQueue { get; } = new();

    private FlashExecutionPlan? _activePlan;
    public FlashExecutionPlan? ActivePlan
    {
        get => _activePlan;
        private set
        {
            if (SetProperty(ref _activePlan, value))
            {
                RunDryRunCommand.RaiseCanExecuteChanged();
                ExecuteFlashCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private DryRunValidationReport? _dryRunReport;
    public DryRunValidationReport? DryRunReport
    {
        get => _dryRunReport;
        private set
        {
            if (SetProperty(ref _dryRunReport, value))
                ExecuteFlashCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _riskAcknowledged;
    public bool RiskAcknowledged
    {
        get => _riskAcknowledged;
        set
        {
            if (SetProperty(ref _riskAcknowledged, value))
                ExecuteFlashCommand.RaiseCanExecuteChanged();
        }
    }

    private string _confirmationText = string.Empty;
    public string ConfirmationText
    {
        get => _confirmationText;
        set
        {
            if (SetProperty(ref _confirmationText, value))
                ExecuteFlashCommand.RaiseCanExecuteChanged();
        }
    }

    private int _progressPercent;
    public int ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }

    private FlashOperationReport? _lastOperationReport;
    public FlashOperationReport? LastOperationReport
    {
        get => _lastOperationReport;
        private set => SetProperty(ref _lastOperationReport, value);
    }

    public RelayCommand AddToQueueCommand { get; }
    public RelayCommand BrowseFlashImageCommand { get; }
    public RelayCommand ClearQueueCommand { get; }
    public AsyncRelayCommand BuildPlanCommand { get; }
    public AsyncRelayCommand RunDryRunCommand { get; }
    public AsyncRelayCommand ExecuteFlashCommand { get; }
    public RelayCommand CancelBetweenPartitionsCommand { get; }

    public FlashManagerViewModel(IDeviceDiscoveryProvider discovery, IFlashManager flashManager)
    {
        _discovery = discovery;
        _flashManager = flashManager;

        BuildPlanCommand = new AsyncRelayCommand(BuildPlanAsync, () => FlashQueue.Count > 0);
        RunDryRunCommand = new AsyncRelayCommand(RunDryRunAsync, () => ActivePlan is { BlockedBySafetyPolicy: false });
        ExecuteFlashCommand = new AsyncRelayCommand(
            ExecuteVerifiedFlashAsync,
            () => ActivePlan is { BlockedBySafetyPolicy: false } &&
                  DryRunReport is { Passed: true } &&
                  RiskAcknowledged &&
                  string.Equals(ConfirmationText?.Trim(), ActivePlan.RequiredConfirmationText, StringComparison.Ordinal));

        CancelBetweenPartitionsCommand = new RelayCommand(
            () => _flashCts?.Cancel(),
            () => IsBusy);

        BrowseFlashImageCommand = new RelayCommand(() =>
        {
            var picked = UiInteractionBridge.PickOpenFile?.Invoke(
                "Select Partition Image (.img / .bin)",
                "Partition Images (*.img;*.bin)|*.img;*.bin|All Files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(picked))
                NewImageFilePath = picked;
        });

        AddToQueueCommand = new RelayCommand(
            () =>
            {
                FlashQueue.Add(new FlashQueueRow { PartitionName = NewPartitionName.Trim(), ImageFilePath = NewImageFilePath.Trim() });
                ActivePlan = null;
                DryRunReport = null;
                BuildPlanCommand.RaiseCanExecuteChanged();
                StatusBanner = $"Queued '{NewPartitionName.Trim()}' ({FlashQueue.Count} item(s)). Build Plan next.";
            },
            () => !string.IsNullOrWhiteSpace(NewPartitionName) && !string.IsNullOrWhiteSpace(NewImageFilePath));

        ClearQueueCommand = new RelayCommand(() =>
        {
            FlashQueue.Clear();
            ActivePlan = null;
            DryRunReport = null;
            BuildPlanCommand.RaiseCanExecuteChanged();
            StatusBanner = "Cleared flash queue.";
        });
    }

    public void Dispose()
    {
        _flashCts?.Dispose();
        _flashCts = null;
    }

    private async Task BuildPlanAsync()
    {
        try
        {
            var dev = _discovery.RequireExplicitlySelectedDevice();
            IsBusy = true;
            DryRunReport = null;
            var tuples = FlashQueue.Select(q => (q.PartitionName, q.ImageFilePath)).ToList();
            ActivePlan = await _flashManager.BuildExecutionPlanAsync(dev, tuples).ConfigureAwait(true);
            StatusBanner = ActivePlan.BlockedBySafetyPolicy
                ? $"Plan blocked by safety guardrails: {(ActivePlan.SafetyFindings.Count > 0 ? ActivePlan.SafetyFindings[0] : "Policy violation")}"
                : $"Plan {ActivePlan.PlanId} ready ({ActivePlan.ProposedSteps.Count} steps). Run Dry-Run Validation next.";
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

    private async Task RunDryRunAsync()
    {
        if (ActivePlan is null) return;
        IsBusy = true;
        try
        {
            DryRunReport = await _flashManager.RunDryRunValidationAsync(ActivePlan).ConfigureAwait(true);
            StatusBanner = DryRunReport.Passed
                ? $"Dry-Run PASSED. Confirm risk and type '{ActivePlan.RequiredConfirmationText}' to enable Write."
                : $"Dry-Run FAILED: {string.Join("; ", DryRunReport.BlockingErrors)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteVerifiedFlashAsync()
    {
        if (ActivePlan is null || DryRunReport is null) return;
        IsBusy = true;
        CancelBetweenPartitionsCommand.RaiseCanExecuteChanged();
        _flashCts = new CancellationTokenSource();

        try
        {
            var token = new FlashConfirmationToken(
                ActivePlan.PlanId,
                ActivePlan.PlanHashSha256,
                ConfirmationText.Trim(),
                RiskAcknowledged,
                DateTimeOffset.UtcNow);

            var prog = new Progress<(int StepIndex, int TotalSteps, string Status)>(p =>
            {
                ProgressPercent = p.TotalSteps > 0 ? (p.StepIndex * 100) / p.TotalSteps : 0;
                StatusBanner = $"[{p.StepIndex}/{p.TotalSteps}] {p.Status}";
            });

            LastOperationReport = await _flashManager.ExecutePlanAsync(
                ActivePlan, DryRunReport, token, prog, _flashCts.Token).ConfigureAwait(true);

            StatusBanner = LastOperationReport.VerificationSummary;
            ConfirmationText = string.Empty;
            RiskAcknowledged = false;
        }
        catch (Exception ex)
        {
            StatusBanner = $"Flash aborted: {ex.Message}";
        }
        finally
        {
            _flashCts.Dispose();
            _flashCts = null;
            IsBusy = false;
            CancelBetweenPartitionsCommand.RaiseCanExecuteChanged();
        }
    }
}

// Page 14: Device Profiles Catalog & Local JSON Importer
public sealed class DeviceProfilesViewModel : PageViewModelBase
{
    private readonly IDeviceProfileRepository _repo;

    public override int PageNumber => 14;
    public override string PageTitle => "Device Profiles";
    public override string NavigationGroup => "PROFILES & TELEMETRY";
    public override string IconGlyph => "📱";

    private string _searchFilter = string.Empty;
    public string SearchFilter
    {
        get => _searchFilter;
        set
        {
            if (SetProperty(ref _searchFilter, value))
                RefreshProfilesList();
        }
    }

    private string _importJsonPath = string.Empty;
    public string ImportJsonPath
    {
        get => _importJsonPath;
        set
        {
            if (SetProperty(ref _importJsonPath, value))
                ImportProfileCommand.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<DeviceProfile> FilteredProfiles { get; } = new();

    private DeviceProfile? _selectedProfile;
    public DeviceProfile? SelectedProfile
    {
        get => _selectedProfile;
        set => SetProperty(ref _selectedProfile, value);
    }

    public AsyncRelayCommand ImportProfileCommand { get; }
    public RelayCommand BrowseProfileJsonCommand { get; }

    public DeviceProfilesViewModel(IDeviceProfileRepository repo)
    {
        _repo = repo;
        BrowseProfileJsonCommand = new RelayCommand(() =>
        {
            var picked = UiInteractionBridge.PickOpenFile?.Invoke("Select Device Profile JSON", "JSON Files (*.json)|*.json|All Files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(picked))
                ImportJsonPath = picked;
        });
        ImportProfileCommand = new AsyncRelayCommand(
            async () =>
            {
                try
                {
                    var imported = await _repo.ImportProfileFromFileAsync(ImportJsonPath).ConfigureAwait(true);
                    RefreshProfilesList();
                    SelectedProfile = imported;
                    StatusBanner = $"Imported profile '{imported.MarketingName}' ({imported.ProfileId}).";
                }
                catch (Exception ex)
                {
                    StatusBanner = $"Import failed: {ex.Message}";
                }
            },
            () => !string.IsNullOrWhiteSpace(ImportJsonPath));

        RefreshProfilesList();
    }

    public void RefreshProfilesList()
    {
        var term = SearchFilter?.Trim() ?? string.Empty;
        FilteredProfiles.Clear();
        foreach (var p in _repo.GetAllProfiles())
        {
            if (string.IsNullOrEmpty(term) ||
                p.MarketingName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Manufacturer.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Codename.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.ChipsetFamily.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.ModelCodes.Any(m => m.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredProfiles.Add(p);
            }
        }
        SelectedProfile ??= FilteredProfiles.FirstOrDefault();
        StatusBanner = $"{FilteredProfiles.Count} profile(s) loaded. Note: Reference profiles are marked honestly as ReferenceSpecification until hardware bench-tested.";
    }
}
