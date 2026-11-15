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
        // Nokia (B4:1D:62) -> From CSV: "Nokia Shanghai Bell Co., Ltd."
        var nokia = MacVendorDatabase.Resolve("B4:1D:62:C2:7F:20");
        Assert.Contains("Nokia", nokia, System.StringComparison.OrdinalIgnoreCase);

        // Xiaomi (88:52:EB) -> From CSV: "Xiaomi Communications Co Ltd"
        var xiaomi = MacVendorDatabase.Resolve("88:52:EB:A7:D9:2A");
        Assert.Contains("Xiaomi", xiaomi, System.StringComparison.OrdinalIgnoreCase);

        // MA-M (28-bit prefix 1C:87:76:D) -> From CSV: "Qivivo"
        var qivivo = MacVendorDatabase.Resolve("1C:87:76:D5:43:21");
        Assert.Equal("Qivivo", qivivo);

        // MA-S (36-bit prefix 00:1B:C5:00:1) -> From CSV: "OpenRB.com, Direct SIA"
        var openrb = MacVendorDatabase.Resolve("00:1B:C5:00:12:34");
        Assert.Equal("OpenRB.com, Direct SIA", openrb);

        // Microsoft (00:15:5D)
        var ms = MacVendorDatabase.Resolve("00:15:5D:D8:9C:E0");
        Assert.Contains("Microsoft", ms, System.StringComparison.OrdinalIgnoreCase);

        // Random address returns "Private address (Random MAC)"
        var random = MacVendorDatabase.Resolve("02:28:D3:2D:C4:D9");
        Assert.Equal("Private address (Random MAC)", random);
    }

    [Fact]
    public async System.Threading.Tasks.Task MacVendorDatabase_LookupOnlineAsync_ResolvesFromCsvFirstWithoutInternet()
    {
        // Should resolve immediately from CSV without performing HTTP requests
        var xiaomi = await MacVendorDatabase.LookupOnlineAsync("88:52:EB:A7:D9:2A");
        Assert.Equal("Xiaomi Communications Co Ltd", xiaomi);
    }

    [Fact]
    public async System.Threading.Tasks.Task MacVendorDatabase_LookupOnlineAsync_RandomMac_ReturnsImmediatelyWithoutApi()
    {
        var random = await MacVendorDatabase.LookupOnlineAsync("02:28:D3:2D:C4:D9");
        Assert.Equal("Private address (Random MAC)", random);
    }
}
