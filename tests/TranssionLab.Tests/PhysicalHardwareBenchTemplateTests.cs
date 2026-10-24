using TranssionLab.Logging;
using TranssionLab.Protocols.Transport;
using TranssionLab.Protocols.Usb;
using Xunit;

namespace TranssionLab.Tests;

/// <summary>
/// Physical bench hardware test suite — strictly separated from SimulatedUnit tests (Section 11).
/// Only executes live hardware assertions when TRANSSIONLAB_RUN_HARDWARE_TESTS=1 is explicitly set
/// on a Windows servicing workstation with a physical TECNO/Infinix/itel handset attached.
/// </summary>
[Trait("Category", "HardwareBench")]
public sealed class PhysicalHardwareBenchTemplateTests
{
    [Fact]
    public async Task LiveWindowsPnpEnumeration_ExecutesCleanlyWhenEnabledOnBench()
    {
        if (Environment.GetEnvironmentVariable("TRANSSIONLAB_RUN_HARDWARE_TESTS") != "1")
        {
            // Explicitly no-op in headless CI so simulated tests and physical hardware verification are never conflated.
            return;
        }

        using var backend = new WindowsWmiPnpBackend();
        var logger = new StructuredLogger();
        var usbProvider = new UsbInterfaceProvider(backend, logger);
        var devices = await usbProvider.EnumerateUsbDevicesAsync();
        Assert.NotNull(devices);
    }
}
