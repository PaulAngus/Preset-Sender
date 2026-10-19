#if TEST_LAB_OFFLINE_WINDOWS
namespace PresetSender.Tests;

public sealed class OfflineMidiBoundaryTests
{
    [Fact]
    public void NativeBoundaryCannotEnumerateOpenOrCreateMidi()
    {
        var backend = new NativeMidiBackend();
        backend.RefreshCapabilities();
        Assert.Empty(backend.GetInputDevices()); Assert.Empty(backend.GetOutputDevices());
        Assert.False(backend.SupportsVirtualOutput);
        Assert.Throws<InvalidOperationException>(() => backend.OpenInput("FM9"));
        Assert.Throws<InvalidOperationException>(() => backend.OpenOutput(0));
        Assert.Throws<InvalidOperationException>(() => backend.OpenVirtualOutput());
    }
}
#endif
