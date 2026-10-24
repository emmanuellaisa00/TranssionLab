using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Protocols.Transport;

namespace TranssionLab.Protocols.Usb;

public sealed class UsbInterfaceProvider : IUsbInterfaceProvider
{
    private readonly IWindowsPnpBackend _pnpBackend;
    private readonly IStructuredLogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, DetectedUsbDevice> _knownDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UsbConnectionHistoryEvent> _history = new();

    public string ProviderName => "USB & Windows PnP Discovery";

    public ProviderImplementationStatus ImplementationStatus =>
        OperatingSystem.IsWindows()
            ? ProviderImplementationStatus.SupportedValidatedWorkflows
            : ProviderImplementationStatus.ReadOnlyDiagnostics;

    public event EventHandler<UsbConnectionHistoryEvent>? UsbDeviceEvent;

    public UsbInterfaceProvider(IWindowsPnpBackend pnpBackend, IStructuredLogger logger)
    {
        _pnpBackend = pnpBackend;
        _logger = logger;
        _pnpBackend.DeviceTopologyChanged += OnTopologyChanged;
    }

    private async void OnTopologyChanged(object? sender, EventArgs e)
    {
        try
        {
            await EnumerateUsbDevicesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ProviderName, "TopologyEventScan", ex.Message);
        }
    }

    public async Task<IReadOnlyList<DetectedUsbDevice>> EnumerateUsbDevicesAsync(CancellationToken ct = default)
    {
        var rawRecords = await _pnpBackend.QueryUsbAndPortEntitiesAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var currentBatch = new Dictionary<string, DetectedUsbDevice>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in rawRecords)
        {
            if (!UsbSignatureCatalog.TryParsePnpDeviceId(raw.DeviceId, out var vid, out var pid, out var mi, out var serial))
                continue;

            if (!UsbSignatureCatalog.IsRelevantServicingDevice(vid, raw.Name))
                continue;

            var (mode, chipset, vendorLabel) = UsbSignatureCatalog.ClassifyInterface(vid, pid, mi, raw.Name);
            var comPort = UsbSignatureCatalog.ExtractComPortFromName(raw.Name);
            var driverDiag = UsbSignatureCatalog.EvaluateDriverHealth(raw.ConfigManagerErrorCode, raw.Service, raw.Manufacturer, mode);

            var ifaceDesc = new UsbInterfaceDescriptor(
                mi ?? 0,
                raw.Service ?? "USB",
                mode.ToString(),
                comPort ?? "Bulk/Control",
                raw.Name,
                mode);

            DateTimeOffset firstSeen = now;
            lock (_gate)
            {
                if (_knownDevices.TryGetValue(raw.DeviceId, out var prior))
                {
                    firstSeen = prior.FirstSeenUtc;
                }
            }

            var detected = new DetectedUsbDevice(
                InstanceId: raw.DeviceId,
                VendorId: vid,
                ProductId: pid,
                CompositeInterfaceNumber: mi,
                SerialNumber: serial,
                FriendlyName: raw.Name,
                ManufacturerLabel: string.IsNullOrWhiteSpace(raw.Manufacturer) || raw.Manufacturer.StartsWith('(')
                    ? vendorLabel
                    : raw.Manufacturer,
                AssociatedComPort: comPort,
                ClassifiedMode: mode,
                DetectedChipsetVendor: chipset,
                Interfaces: [ifaceDesc],
                DriverDiagnostics: driverDiag,
                FirstSeenUtc: firstSeen,
                LastSeenUtc: now);

            currentBatch[raw.DeviceId] = detected;
        }

        var emittedEvents = new List<UsbConnectionHistoryEvent>();
        lock (_gate)
        {
            // Arrivals and mode changes
            foreach (var (id, dev) in currentBatch)
            {
                if (!_knownDevices.TryGetValue(id, out var previous))
                {
                    var evt = new UsbConnectionHistoryEvent(
                        now, UsbEventKind.Arrived, id, dev.VidPidDisplay, dev.FriendlyName, dev.ClassifiedMode,
                        $"Arrived ({dev.AssociatedComPort ?? "USB"}); Driver={dev.DriverDiagnostics.Status}");
                    _history.Add(evt);
                    emittedEvents.Add(evt);

                    if (dev.DriverDiagnostics.Status != DriverHealthStatus.Healthy)
                    {
                        var drvEvt = new UsbConnectionHistoryEvent(
                            now, UsbEventKind.DriverProblemDetected, id, dev.VidPidDisplay, dev.FriendlyName, dev.ClassifiedMode,
                            dev.DriverDiagnostics.Summary);
                        _history.Add(drvEvt);
                        emittedEvents.Add(drvEvt);
                    }
                }
                else if (previous.ClassifiedMode != dev.ClassifiedMode)
                {
                    var evt = new UsbConnectionHistoryEvent(
                        now, UsbEventKind.ModeChanged, id, dev.VidPidDisplay, dev.FriendlyName, dev.ClassifiedMode,
                        $"Transitioned {previous.ClassifiedMode} -> {dev.ClassifiedMode}");
                    _history.Add(evt);
                    emittedEvents.Add(evt);
                }
            }

            // Removals
            foreach (var (oldId, oldDev) in _knownDevices)
            {
                if (!currentBatch.ContainsKey(oldId))
                {
                    var evt = new UsbConnectionHistoryEvent(
                        now, UsbEventKind.Removed, oldId, oldDev.VidPidDisplay, oldDev.FriendlyName, oldDev.ClassifiedMode,
                        "USB interface disconnected");
                    _history.Add(evt);
                    emittedEvents.Add(evt);
                }
            }

            _knownDevices.Clear();
            foreach (var (k, v) in currentBatch)
            {
                _knownDevices[k] = v;
            }
        }

        foreach (var evt in emittedEvents)
        {
            _logger.Log(new StructuredLogEntry
            {
                TimestampUtc = evt.TimestampUtc,
                SessionId = _logger.CurrentSessionId,
                SequenceNumber = 0,
                Severity = evt.EventKind == UsbEventKind.DriverProblemDetected ? LogSeverity.Warning : LogSeverity.Info,
                DeviceId = evt.InstanceId,
                ProtocolProvider = ProviderName,
                UsbInterface = evt.VidPid,
                RequestedAction = $"USB {evt.EventKind}",
                ConnectionChange = evt.Details
            });
            UsbDeviceEvent?.Invoke(this, evt);
        }

        return currentBatch.Values.OrderBy(d => d.FriendlyName).ToList();
    }

    public async Task<IReadOnlyList<ComPortDescriptor>> EnumerateComPortsAsync(CancellationToken ct = default)
    {
        var usbDevices = await EnumerateUsbDevicesAsync(ct).ConfigureAwait(false);
        var ports = new Dictionary<string, ComPortDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var usb in usbDevices.Where(u => !string.IsNullOrWhiteSpace(u.AssociatedComPort)))
        {
            var port = usb.AssociatedComPort!;
            ports[port] = new ComPortDescriptor(
                port,
                usb.FriendlyName,
                usb.VendorId,
                usb.ProductId,
                usb.InstanceId,
                usb.DriverDiagnostics.DriverProvider,
                usb.ClassifiedMode);
        }

        foreach (var sysPort in _pnpBackend.GetSystemComPortNames())
        {
            if (!ports.ContainsKey(sysPort))
            {
                ports[sysPort] = new ComPortDescriptor(
                    sysPort,
                    "Standard Serial / Virtual COM Port",
                    null,
                    null,
                    null,
                    null,
                    ConnectionMode.Unknown);
            }
        }

        return ports.Values.OrderBy(p => p.PortName).ToList();
    }

    public IReadOnlyList<UsbConnectionHistoryEvent> GetConnectionHistory()
    {
        lock (_gate)
        {
            return _history.ToList();
        }
    }

    public void StartMonitoring() => _pnpBackend.StartWatching();
    public void StopMonitoring() => _pnpBackend.StopWatching();
}
