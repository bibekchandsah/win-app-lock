using AppLock.Services.SpeedMeter;

namespace AppLock.Tests.SpeedMeter;

public class SpeedFormatterTests
{
    [Fact]
    public void Auto_ShowsRawBytesBelowOneKilobyte()
    {
        Assert.Equal("812 B/s", SpeedFormatter.Format(812, "Auto", 1));
    }

    [Fact]
    public void Auto_ScalesToKilobytes()
    {
        Assert.Equal("2.3 KB/s", SpeedFormatter.Format(2400, "Auto", 1));
    }

    [Fact]
    public void Bytes_KeepsDecimalsBelowOneKilobyte()
    {
        Assert.Equal("812.0 B/s", SpeedFormatter.Format(812, "Bytes", 1));
    }

    [Fact]
    public void Bits_UsesMegabits()
    {
        Assert.Equal("10.0 Mbps", SpeedFormatter.Format(1_250_000, "Bits", 1));
    }

    [Fact]
    public void Bits_UsesKilobitsBelowOneMegabit()
    {
        Assert.Equal("8.0 Kbps", SpeedFormatter.Format(1000, "Bits", 1));
    }

    [Fact]
    public void Decimals_AreClampedToTwo()
    {
        Assert.Equal("2.34 KB/s", SpeedFormatter.Format(2400, "Auto", 5));
    }

    [Fact]
    public void NegativeRatesReadAsZero()
    {
        Assert.Equal("0 B/s", SpeedFormatter.Format(-100, "Auto", 1));
    }
}
