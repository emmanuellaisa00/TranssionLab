using System.Collections.ObjectModel;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.ViewModels.Mvvm;

namespace TranssionLab.ViewModels.Pages;

// Page 6: MediaTek Tools (Section 2B)
public sealed class MediaTekToolsViewModel : PageViewModelBase
{
    private readonly IUsbInterfaceProvider _usb;
    private readonly IMediaTekProvider _mtk;

    public override int PageNumber => 6;
    public override string PageTitle => "MediaTek Tools";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "Ⓜ";

    public ObservableCollection<DetectedUsbDevice> DetectedMtkEndpoints { get; } = new();

    private string _selectedComPort = string.Empty;
    public string SelectedComPort
    {
        get => _selectedComPort;
        set
        {
            if (SetProperty(ref _selectedComPort, value))
                ProbeReadOnlyBromCommand.RaiseCanExecuteChanged();
        }
    }

    private MediaTekHandshakeProbeResult? _lastProbeResult;
    public MediaTekHandshakeProbeResult? LastProbeResult
    {
        get => _lastProbeResult;
        private set => SetProperty(ref _lastProbeResult, value);
    }

    public AsyncRelayCommand ScanMtkInterfacesCommand { get; }
    public AsyncRelayCommand ProbeReadOnlyBromCommand { get; }

    public MediaTekToolsViewModel(IUsbInterfaceProvider usb, IMediaTekProvider mtk)
    {
        _usb = usb;
        _mtk = mtk;
        ScanMtkInterfacesCommand = new AsyncRelayCommand(RefreshMtkAsync);
        ProbeReadOnlyBromCommand = new AsyncRelayCommand(
            async () =>
            {
                IsBusy = true;
                try
                {
                    LastProbeResult = await _mtk.ProbeReadOnlyBromOrPreloaderAsync(SelectedComPort).ConfigureAwait(true);
                    StatusBanner = LastProbeResult.DiagnosticMessage;
                }
                finally
                {
                    IsBusy = false;
                }
            },
            () => !string.IsNullOrWhiteSpace(SelectedComPort));
    }

    public override async Task OnNavigatedToAsync(CancellationToken ct = default) => await RefreshMtkAsync().ConfigureAwait(true);

    private async Task RefreshMtkAsync()
    {
        var all = await _usb.EnumerateUsbDevicesAsync().ConfigureAwait(true);
        var filtered = _mtk.FilterMediaTekInterfaces(all);
        DetectedMtkEndpoints.Clear();
        foreach (var item in filtered)
        {
            DetectedMtkEndpoints.Add(item);
            if (string.IsNullOrWhiteSpace(SelectedComPort) && !string.IsNullOrWhiteSpace(item.AssociatedComPort))
            {
                SelectedComPort = item.AssociatedComPort!;
            }
        }
        StatusBanner = filtered.Count > 0
            ? $"Detected {filtered.Count} MediaTek USB endpoint(s). Detection does NOT imply unrestricted storage/DA access."
            : "No MediaTek BROM (0E8D:0003), Preloader (0E8D:2000), or DA (0E8D:2001) endpoints currently detected.";
    }
}

// Page 7: Dedicated META Mode Panel (Section 2D)
public sealed class MetaModeViewModel : PageViewModelBase
{
    private readonly IUsbInterfaceProvider _usb;
    private readonly IMetaModeProvider _meta;

    public override int PageNumber => 7;
    public override string PageTitle => "META Mode";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "⚙";

    private string _providerInstallationStatus = string.Empty;
    public string ProviderInstallationStatus { get => _providerInstallationStatus; private set => SetProperty(ref _providerInstallationStatus, value); }

    public ObservableCollection<MetaInterfaceDetection> DetectedMetaEndpoints { get; } = new();

    private MetaInterfaceDetection? _selectedMetaEndpoint;
    public MetaInterfaceDetection? SelectedMetaEndpoint
    {
        get => _selectedMetaEndpoint;
        set
        {
            if (SetProperty(ref _selectedMetaEndpoint, value))
                VerifyMetaSessionCommand.RaiseCanExecuteChanged();
        }
    }

    private MetaSessionDiagnosticResult? _lastDiagnostic;
    public MetaSessionDiagnosticResult? LastDiagnostic
    {
        get => _lastDiagnostic;
        private set => SetProperty(ref _lastDiagnostic, value);
    }

    public AsyncRelayCommand ScanMetaEndpointsCommand { get; }
    public AsyncRelayCommand VerifyMetaSessionCommand { get; }

    public MetaModeViewModel(IUsbInterfaceProvider usb, IMetaModeProvider meta)
    {
        _usb = usb;
        _meta = meta;
        ScanMetaEndpointsCommand = new AsyncRelayCommand(RefreshMetaAsync);
        VerifyMetaSessionCommand = new AsyncRelayCommand(
            async () =>
            {
                if (SelectedMetaEndpoint is null) return;
                LastDiagnostic = await _meta.DiagnoseMetaConnectionAsync(SelectedMetaEndpoint.UsbDevice).ConfigureAwait(true);
                StatusBanner = LastDiagnostic.DiagnosticSummary;
            },
            () => SelectedMetaEndpoint is not null);
    }

    public override async Task OnNavigatedToAsync(CancellationToken ct = default) => await RefreshMetaAsync().ConfigureAwait(true);

    private async Task RefreshMetaAsync()
    {
        _meta.IsAuthorizedMetaLibraryInstalled(out var detail);
        ProviderInstallationStatus = detail;

        var usbList = await _usb.EnumerateUsbDevicesAsync().ConfigureAwait(true);
        var detections = _meta.DetectMetaInterfaces(usbList);
        DetectedMetaEndpoints.Clear();
        foreach (var d in detections) DetectedMetaEndpoints.Add(d);
        SelectedMetaEndpoint = DetectedMetaEndpoints.FirstOrDefault();

        StatusBanner = detections.Count > 0
            ? $"Found {detections.Count} recognized META USB endpoint(s)."
            : "No MediaTek META USB interface detected. Generic USB devices are never fabricated as META.";
    }
}

// Page 8: SPD / Unisoc Tools (Section 2C)
public sealed class SpdUnisocToolsViewModel : PageViewModelBase
{
    private readonly IUsbInterfaceProvider _usb;
    private readonly ISpdUnisocProvider _unisoc;

    public override int PageNumber => 8;
    public override string PageTitle => "SPD/Unisoc Tools";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "Ⓤ";

    public ObservableCollection<DetectedUsbDevice> DetectedUnisocEndpoints { get; } = new();

    private string _selectedComPort = string.Empty;
    public string SelectedComPort
    {
        get => _selectedComPort;
        set
        {
            if (SetProperty(ref _selectedComPort, value))
                ProbeDownloadBannerCommand.RaiseCanExecuteChanged();
        }
    }

    private UnisocBootProbeResult? _lastProbeResult;
    public UnisocBootProbeResult? LastProbeResult
    {
        get => _lastProbeResult;
        private set => SetProperty(ref _lastProbeResult, value);
    }

    public AsyncRelayCommand ScanUnisocEndpointsCommand { get; }
    public AsyncRelayCommand ProbeDownloadBannerCommand { get; }

    public SpdUnisocToolsViewModel(IUsbInterfaceProvider usb, ISpdUnisocProvider unisoc)
    {
        _usb = usb;
        _unisoc = unisoc;
        ScanUnisocEndpointsCommand = new AsyncRelayCommand(RefreshUnisocAsync);
        ProbeDownloadBannerCommand = new AsyncRelayCommand(
            async () =>
            {
                IsBusy = true;
                try
                {
                    LastProbeResult = await _unisoc.ProbeReadOnlyDownloadVersionAsync(SelectedComPort).ConfigureAwait(true);
                    StatusBanner = LastProbeResult.DiagnosticMessage;
                }
                finally
                {
                    IsBusy = false;
                }
            },
            () => !string.IsNullOrWhiteSpace(SelectedComPort));
    }

    public override async Task OnNavigatedToAsync(CancellationToken ct = default) => await RefreshUnisocAsync().ConfigureAwait(true);

    private async Task RefreshUnisocAsync()
    {
        var all = await _usb.EnumerateUsbDevicesAsync().ConfigureAwait(true);
        var filtered = _unisoc.FilterUnisocInterfaces(all);
        DetectedUnisocEndpoints.Clear();
        foreach (var item in filtered)
        {
            DetectedUnisocEndpoints.Add(item);
            if (string.IsNullOrWhiteSpace(SelectedComPort) && !string.IsNullOrWhiteSpace(item.AssociatedComPort))
                SelectedComPort = item.AssociatedComPort!;
        }
        StatusBanner = filtered.Count > 0
            ? $"Detected {filtered.Count} Unisoc/Spreadtrum USB endpoint(s) (VID 0x1782)."
            : "No Unisoc Download (1782:4D00) or SCI Diagnostic endpoints currently connected.";
    }
}

// Page 9: DIAG and Diagnostics (Section 2E)
public sealed class DiagDiagnosticsViewModel : PageViewModelBase
{
    private readonly IUsbInterfaceProvider _usb;
    private readonly IDiagnosticProvider _diag;

    public override int PageNumber => 9;
    public override string PageTitle => "DIAG and Diagnostics";
    public override string NavigationGroup => "PROTOCOLS & MODES";
    public override string IconGlyph => "∿";

    public ObservableCollection<DiagInterfaceAssessment> ClassifiedDiagEndpoints { get; } = new();

    private DiagInterfaceAssessment? _selectedEndpoint;
    public DiagInterfaceAssessment? SelectedEndpoint
    {
        get => _selectedEndpoint;
        set
        {
            if (SetProperty(ref _selectedEndpoint, value))
                RunReadOnlyVersionQueryCommand.RaiseCanExecuteChanged();
        }
    }

    private DiagReadOnlyQueryResult? _lastQueryResult;
    public DiagReadOnlyQueryResult? LastQueryResult
    {
        get => _lastQueryResult;
        private set => SetProperty(ref _lastQueryResult, value);
    }

    public AsyncRelayCommand ScanDiagEndpointsCommand { get; }
    public AsyncRelayCommand RunReadOnlyVersionQueryCommand { get; }

    public DiagDiagnosticsViewModel(IUsbInterfaceProvider usb, IDiagnosticProvider diag)
    {
        _usb = usb;
        _diag = diag;
        ScanDiagEndpointsCommand = new AsyncRelayCommand(RefreshDiagAsync);
        RunReadOnlyVersionQueryCommand = new AsyncRelayCommand(
            async () =>
            {
                if (SelectedEndpoint is null) return;
                IsBusy = true;
                try
                {
                    LastQueryResult = await _diag.QueryReadOnlyDiagnosticVersionAsync(SelectedEndpoint).ConfigureAwait(true);
                    StatusBanner = LastQueryResult.Summary;
                }
                finally
                {
                    IsBusy = false;
                }
            },
            () => SelectedEndpoint is { SupportsReadOnlyVersionProbe: true });
    }

    public override async Task OnNavigatedToAsync(CancellationToken ct = default) => await RefreshDiagAsync().ConfigureAwait(true);

    private async Task RefreshDiagAsync()
    {
        var usbList = await _usb.EnumerateUsbDevicesAsync().ConfigureAwait(true);
        var items = _diag.ClassifyDiagnosticInterfaces(usbList);
        ClassifiedDiagEndpoints.Clear();
        foreach (var i in items) ClassifiedDiagEndpoints.Add(i);
        SelectedEndpoint = ClassifiedDiagEndpoints.FirstOrDefault();

        StatusBanner = items.Count > 0
            ? $"Classified {items.Count} diagnostic endpoint(s). Protected NV/radio writes are strictly disabled."
            : "No DIAG USB interface detected. Note: MediaTek and Unisoc Transsion devices do NOT expose Qualcomm QCDM.";
    }
}

// Page 10: Bootloader Manager — Android 16 & Earlier (Section 5)
public sealed class BootloaderManagerViewModel : PageViewModelBase
{
    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IBootloaderManager _bootloaderManager;

    public override int PageNumber => 10;
    public override string PageTitle => "Bootloader Manager";
    public override string NavigationGroup => "SERVICING & FIRMWARE";
    public override string IconGlyph => "🔓";

    private BootloaderAssessment? _currentAssessment;
    public BootloaderAssessment? CurrentAssessment
    {
        get => _currentAssessment;
        private set
        {
            if (SetProperty(ref _currentAssessment, value))
            {
                ExecuteOfficialUnlockCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _confirmationInput = string.Empty;
    public string ConfirmationInput
    {
        get => _confirmationInput;
        set
        {
            if (SetProperty(ref _confirmationInput, value))
                ExecuteOfficialUnlockCommand.RaiseCanExecuteChanged();
        }
    }

    private BootloaderOperationResult? _lastResult;
    public BootloaderOperationResult? LastResult
    {
        get => _lastResult;
        private set => SetProperty(ref _lastResult, value);
    }

    public AsyncRelayCommand AssessBootloaderCommand { get; }
    public AsyncRelayCommand ExecuteOfficialUnlockCommand { get; }

    public BootloaderManagerViewModel(IDeviceDiscoveryProvider discovery, IBootloaderManager bootloaderManager)
    {
        _discovery = discovery;
        _bootloaderManager = bootloaderManager;

        AssessBootloaderCommand = new AsyncRelayCommand(AssessAsync);
        ExecuteOfficialUnlockCommand = new AsyncRelayCommand(
            ExecuteUnlockAsync,
            () => CurrentAssessment is { IsUnlockWorkflowPermitted: true } &&
                  string.Equals(ConfirmationInput?.Trim(), CurrentAssessment.ExpectedConfirmationPhrase, StringComparison.Ordinal));

        _discovery.InventoryChanged += (_, _) => _ = AssessIfSelectedAsync();
    }

    public override async Task OnNavigatedToAsync(CancellationToken ct = default) => await AssessIfSelectedAsync().ConfigureAwait(true);

    private async Task AssessIfSelectedAsync()
    {
        if (_discovery.SelectedDevice is not null)
        {
            await AssessAsync().ConfigureAwait(true);
        }
        else
        {
            CurrentAssessment = null;
            StatusBanner = _discovery.RequiresExplicitSelection
                ? "Select one device explicitly before assessing bootloader state."
                : "Connect a supported device in ADB or Fastboot mode to evaluate bootloader state.";
        }
    }

    private async Task AssessAsync()
    {
        try
        {
            var dev = _discovery.RequireExplicitlySelectedDevice();
            IsBusy = true;
            CurrentAssessment = await _bootloaderManager.AssessDeviceAsync(dev).ConfigureAwait(true);
            StatusBanner = CurrentAssessment.StatusSummary;
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

    private async Task ExecuteUnlockAsync()
    {
        if (CurrentAssessment is null) return;
        try
        {
            var dev = _discovery.RequireExplicitlySelectedDevice();
            IsBusy = true;
            LastResult = await _bootloaderManager.ExecuteVerifiedUnlockAsync(dev, CurrentAssessment, ConfirmationInput).ConfigureAwait(true);
            StatusBanner = LastResult.Summary;
            ConfirmationInput = string.Empty;
            CurrentAssessment = await _bootloaderManager.AssessDeviceAsync(dev).ConfigureAwait(true);
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
