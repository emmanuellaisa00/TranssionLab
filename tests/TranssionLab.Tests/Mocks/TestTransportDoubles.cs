using TranssionLab.Core.Interfaces;
using TranssionLab.Protocols.Transport;

namespace TranssionLab.Tests.Mocks;

public sealed class SimulatedPnpBackend : IWindowsPnpBackend
{
    public List<RawPnpDeviceRecord> Devices { get; set; } = new();
    public List<string> ComPorts { get; set; } = new();

    public event EventHandler? DeviceTopologyChanged;

    public Task<IReadOnlyList<RawPnpDeviceRecord>> QueryUsbAndPortEntitiesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RawPnpDeviceRecord>>(Devices.ToList());

    public IReadOnlyList<string> GetSystemComPortNames() => ComPorts.ToList();

    public void TriggerTopologyChange() => DeviceTopologyChanged?.Invoke(this, EventArgs.Empty);
    public void StartWatching() { }
    public void StopWatching() { }
}

public sealed class RecordedProcessRunner : IProcessRunner
{
    private readonly Func<string, IReadOnlyList<string>, CommandExecutionResult> _handler;
    public List<(string Exe, IReadOnlyList<string> Args)> Invocations { get; } = new();

    public RecordedProcessRunner(Func<string, IReadOnlyList<string>, CommandExecutionResult> handler)
    {
        _handler = handler;
    }

    public Task<CommandExecutionResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        Invocations.Add((executablePath, arguments.ToList()));
        return Task.FromResult(_handler(executablePath, arguments));
    }
}

public sealed class SimulatedSerialChannelFactory : ISerialChannelFactory
{
    private readonly Func<string, byte[], byte[]> _responder;

    public SimulatedSerialChannelFactory(Func<string, byte[], byte[]> responder)
    {
        _responder = responder;
    }

    public ISerialByteChannel Create(string portName, int baudRate, TimeSpan readTimeout, TimeSpan writeTimeout) =>
        new MemorySerialChannel(portName, _responder);

    private sealed class MemorySerialChannel : ISerialByteChannel
    {
        private readonly string _port;
        private readonly Func<string, byte[], byte[]> _responder;
        private readonly Queue<byte> _rxQueue = new();

        public MemorySerialChannel(string port, Func<string, byte[], byte[]> responder)
        {
            _port = port;
            _responder = responder;
        }

        public bool IsOpen { get; private set; }
        public void Open() => IsOpen = true;

        public Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var reply = _responder(_port, buffer.ToArray());
            foreach (var b in reply)
            {
                _rxQueue.Enqueue(b);
            }
            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int count = 0;
            var span = buffer.Span;
            while (count < span.Length && _rxQueue.Count > 0)
            {
                span[count++] = _rxQueue.Dequeue();
            }
            return Task.FromResult(count);
        }

        public void Dispose() => IsOpen = false;
    }
}
