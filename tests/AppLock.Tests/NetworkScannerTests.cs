using AppLock.Services.Network;
using Xunit;

namespace AppLock.Tests;

public class NetworkScannerTests
{
    [Theory]
    [InlineData("02:28:D3:2D:C4:D9", true)]
    [InlineData("16:58:6E:70:6A:6D", true)]
    [InlineData("6E:D7:50:D0:5F:F5", true)]
    [InlineData("00:22:6D:E1:DA:83", false)]
    [InlineData("B4:1D:62:C2:7F:20", false)]
    [InlineData("00:15:5D:D8:9C:E0", false)]
    public void MacVendorDatabase_IsRandomMac_IdentifiesLocallyAdministeredAddresses(string mac, bool expectedRandom)
    {
        Assert.Equal(expectedRandom, MacVendorDatabase.IsRandomMac(mac));
    }

    [Fact]
    public void MacVendorDatabase_Resolve_ResolvesKnownVendors()
    {
        // Nokia (B4:1D:62)
        var nokia = MacVendorDatabase.Resolve("B4:1D:62:C2:7F:20");
        Assert.Equal("Nokia", nokia);

        // Motorola (00:22:6D)
        var motorola = MacVendorDatabase.Resolve("00:22:6D:E1:DA:83");
        Assert.Equal("Motorola", motorola);

        // Microsoft (00:15:5D)
        var ms = MacVendorDatabase.Resolve("00:15:5D:D8:9C:E0");
        Assert.Equal("Microsoft", ms);

        // Random address returns "Private address (Random MAC)"
        var random = MacVendorDatabase.Resolve("02:28:D3:2D:C4:D9");
        Assert.Equal("Private address (Random MAC)", random);
    }
}
