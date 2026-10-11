using Nova.Server.Services;

namespace Nova.Tests;

public class NetworkTests
{
    [Fact]
    public void ReadsNetworkManager()
    {
        var output = "  :Neighbours:40\n*:Home\\:5G:72\n  :Other:20\n";
        var r = NetworkService.ParseNmcli(output)!;
        Assert.Equal(("client", "Home:5G", 72), (r.Mode, r.Ssid, r.SignalPercent));
        Assert.Null(NetworkService.ParseNmcli("  :Neighbours:40\n"));
    }

    [Fact]
    public void ReadsIwdIncludingItsColours()
    {
        var output = """
                                         Station: wlan0
            --------------------------------------------------------------------------------
              Settable  Property              Value
            --------------------------------------------------------------------------------
                        Scanning              no
                        State                 connected
                        Connected network     Observatory WiFi
                        IPv4 address          192.168.1.50
                        Security              WPA2-Personal
                        RSSI                  -61 dBm
                        AverageRSSI           -60 dBm
            """.Replace("connected", "\x1b[1;32mconnected\x1b[0m");
        var r = NetworkService.ParseIwctl(output)!;
        Assert.Equal("Observatory WiFi", r.Ssid);
        Assert.Equal(78, r.SignalPercent); // -61 dBm
        Assert.Null(NetworkService.ParseIwctl("  State                 disconnected\n"));
    }

    [Fact]
    public void ReadsIwLinkAndSpotsAHotspot()
    {
        var link = "Connected to 11:22:33:44:55:66 (on wlan0)\n\tSSID: Backyard\n\tfreq: 2437\n\tsignal: -48 dBm\n";
        Assert.Equal(("Backyard", 100), (NetworkService.ParseIwLink(link)!.Ssid, NetworkService.ParseIwLink(link)!.SignalPercent));
        Assert.Null(NetworkService.ParseIwLink("Not connected.\n"));

        var ap = "Interface wlan0\n\tifindex 3\n\ttype AP\n\tssid NOVA-minipc\n\tchannel 6 (2437 MHz)\n";
        var hotspot = NetworkService.ParseIwInfo(ap)!;
        Assert.Equal(("hotspot", "NOVA-minipc"), (hotspot.Mode, hotspot.Ssid));
        Assert.Null(NetworkService.ParseIwInfo("Interface wlan0\n\ttype managed\n"));
    }

    [Fact]
    public void ReadsWindowsNetsh()
    {
        var output = "    Name                   : Wi-Fi\n    State                  : connected\n    SSID                   : Home\n    BSSID                  : aa:bb\n    Signal                 : 91%\n";
        var r = NetworkService.ParseNetsh(output)!;
        Assert.Equal(("Home", 91), (r.Ssid, r.SignalPercent));
        Assert.Null(NetworkService.ParseNetsh("    State                  : disconnected\n"));
    }

    [Fact]
    public async Task ReadsThisMachine()
    {
        var status = await NetworkService.ReadAsync(CancellationToken.None);
        Assert.NotNull(status.Hostname);
        Assert.All(status.Interfaces, i => Assert.NotEmpty(i.Addresses));
    }
}
