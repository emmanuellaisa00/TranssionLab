using System.Diagnostics;
using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using TranssionLab.Core.Interfaces;

namespace TranssionLab.Protocols.Transport;

public static class PlatformToolLocator
{
    public static string ResolveToolExecutable(string? configuredSetting, string defaultCommandName, string? workspaceDataDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredSetting) &&
            !string.Equals(configuredSetting.Trim(), defaultCommandName, StringComparison.OrdinalIgnoreCase))
        {
            return configuredSetting.Trim();
        }

        var exeName = OperatingSystem.IsWindows() ? $"{defaultCommandName}.exe" : defaultCommandName;
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "tools", exeName),
            Path.Combine(baseDir, "platform-tools", exeName),
            Path.Combine(baseDir, exeName)
        };

        if (!string.IsNullOrWhiteSpace(workspaceDataDirectory))
        {
            candidates.Add(Path.Combine(workspaceDataDirectory, "tools", exeName));
            candidates.Add(Path.Combine(workspaceDataDirectory, "platform-tools", exeName));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "Android", "Sdk", "platform-tools", exeName));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return defaultCommandName;
    }
}

public interface IProcessRunner
{
    Task<CommandExecutionResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct = default);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<CommandExecutionResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return new CommandExecutionResult(false, -1, string.Empty, $"Failed to start process '{executablePath}'.", sw.ElapsedMilliseconds);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
                return new CommandExecutionResult(false, -2, string.Empty, $"Command '{executablePath}' timed out after {timeout.TotalSeconds:F1}s.", sw.ElapsedMilliseconds);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            sw.Stop();

            return new CommandExecutionResult(
                process.ExitCode == 0,
                process.ExitCode,
                stdout,
                stderr,
                sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CommandExecutionResult(false, -99, string.Empty, ex.Message, sw.ElapsedMilliseconds);
        }
    }
}

public sealed record RawPnpDeviceRecord(
    string DeviceId,
    string Name,
    string Manufacturer,
    string? Service,
    int? ConfigManagerErrorCode,
    string? Status);

public interface IWindowsPnpBackend
{
    event EventHandler? DeviceTopologyChanged;
    Task<IReadOnlyList<RawPnpDeviceRecord>> QueryUsbAndPortEntitiesAsync(CancellationToken ct = default);
    IReadOnlyList<string> GetSystemComPortNames();
    void StartWatching();
    void StopWatching();
}

public sealed class WindowsWmiPnpBackend : IWindowsPnpBackend, IDisposable
{
    private ManagementEventWatcher? _arrivalWatcher;
    private ManagementEventWatcher? _removalWatcher;

    public event EventHandler? DeviceTopologyChanged;

    public Task<IReadOnlyList<RawPnpDeviceRecord>> QueryUsbAndPortEntitiesAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<RawPnpDeviceRecord>>(Array.Empty<RawPnpDeviceRecord>());
        }

        return Task.Run(QueryWindowsWmiInternal, ct);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<RawPnpDeviceRecord> QueryWindowsWmiInternal()
    {
        var results = new List<RawPnpDeviceRecord>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, Manufacturer, Service, ConfigManagerErrorCode, Status FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'");
            using var collection = searcher.Get();
            foreach (var obj in collection)
            {
                if (obj is not ManagementObject mo)
                    continue;

                var deviceId = mo["DeviceID"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(deviceId))
                    continue;

                var name = mo["Name"]?.ToString() ?? "USB Device";
                var mfg = mo["Manufacturer"]?.ToString() ?? "Unknown";
                var service = mo["Service"]?.ToString();
                int? errCode = mo["ConfigManagerErrorCode"] is uint u ? (int)u :
                               mo["ConfigManagerErrorCode"] is int i ? i : null;
                var status = mo["Status"]?.ToString();

                results.Add(new RawPnpDeviceRecord(deviceId, name, mfg, service, errCode, status));
            }
        }
        catch
        {
            // WMI unavailable or restricted; return partial results safely
        }

        return results;
    }

    public IReadOnlyList<string> GetSystemComPortNames()
    {
        try
        {
            return SerialPort.GetPortNames();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void StartWatching()
    {
        if (!OperatingSystem.IsWindows())
            return;

        StartWindowsWatchers();
    }

    [SupportedOSPlatform("windows")]
    private void StartWindowsWatchers()
    {
        try
        {
            StopWindowsWatchers();
            var arrivalQuery = new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'");
            _arrivalWatcher = new ManagementEventWatcher(arrivalQuery);
            _arrivalWatcher.EventArrived += (_, _) => DeviceTopologyChanged?.Invoke(this, EventArgs.Empty);
            _arrivalWatcher.Start();

            var removalQuery = new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'");
            _removalWatcher = new ManagementEventWatcher(removalQuery);
            _removalWatcher.EventArrived += (_, _) => DeviceTopologyChanged?.Invoke(this, EventArgs.Empty);
            _removalWatcher.Start();
        }
        catch
        {
            // Fall back cleanly to polling when WMI event subscription is restricted
        }
    }

    public void StopWatching()
    {
        if (OperatingSystem.IsWindows())
        {
            StopWindowsWatchers();
        }
    }

    [SupportedOSPlatform("windows")]
    private void StopWindowsWatchers()
    {
        try { _arrivalWatcher?.Stop(); _arrivalWatcher?.Dispose(); } catch { }
        try { _removalWatcher?.Stop(); _removalWatcher?.Dispose(); } catch { }
        _arrivalWatcher = null;
        _removalWatcher = null;
    }

    public void Dispose() => StopWatching();
}

public interface ISerialByteChannel : IDisposable
{
    bool IsOpen { get; }
    void Open();
    Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
}

public interface ISerialChannelFactory
{
    ISerialByteChannel Create(string portName, int baudRate, TimeSpan readTimeout, TimeSpan writeTimeout);
}

public sealed class SystemSerialChannelFactory : ISerialChannelFactory
{
    public ISerialByteChannel Create(string portName, int baudRate, TimeSpan readTimeout, TimeSpan writeTimeout) =>
        new SystemSerialPortChannel(portName, baudRate, readTimeout, writeTimeout);

    private sealed class SystemSerialPortChannel : ISerialByteChannel
    {
        private readonly SerialPort _port;

        public SystemSerialPortChannel(string portName, int baudRate, TimeSpan readTimeout, TimeSpan writeTimeout)
        {
            _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = (int)Math.Max(100, readTimeout.TotalMilliseconds),
                WriteTimeout = (int)Math.Max(100, writeTimeout.TotalMilliseconds),
                DtrEnable = true,
                RtsEnable = true
            };
        }

        public bool IsOpen => _port.IsOpen;
        public void Open() => _port.Open();

        public async Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await _port.BaseStream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await _port.BaseStream.FlushAsync(ct).ConfigureAwait(false);
        }

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_port.ReadTimeout);
            return await _port.BaseStream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
        }

        public void Dispose()
        {
            try { if (_port.IsOpen) _port.Close(); } catch { }
            _port.Dispose();
        }
    }
}
