using MangaJaNaiConverterGui.Services;
using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    public class LinuxMonitorsTests : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), "drm-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        // A 128-byte EDID with a monitor name descriptor (0xFC) in the second descriptor slot.
        static byte[] Edid(string? name)
        {
            var edid = new byte[128];
            if (name != null)
            {
                edid[72 + 3] = 0xFC;
                var text = Encoding.ASCII.GetBytes((name + "\n").PadRight(13));
                Array.Copy(text, 0, edid, 72 + 5, 13);
            }
            return edid;
        }

        void Connector(string name, string status, string modes, byte[]? edid)
        {
            var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
            File.WriteAllText(Path.Combine(dir, "status"), status + "\n");
            File.WriteAllText(Path.Combine(dir, "modes"), modes);
            File.WriteAllBytes(Path.Combine(dir, "edid"), edid ?? []);
        }

        // As on a real machine (Ubuntu 26.04, GNOME Wayland): one connected HDMI monitor among many
        // disconnected connectors, the native mode listed first.
        [Fact]
        public void ConnectedConnectorsGiveTheNativeModeAndTheEdidName()
        {
            Connector("card0-DP-1", "disconnected", "", null);
            Connector("card1-HDMI-A-2", "connected", "1920x1080\n1920x1080\n1680x1050\n1280x1024\n", Edid("ASUS VS247"));
            Connector("card1-DP-4", "connected", "3840x2160\n2560x1440\n", Edid(null));
            Directory.CreateDirectory(Path.Combine(_root, "card0"));

            var monitors = LinuxMonitors.Read(_root);
            Assert.Equal([new LinuxMonitors.Monitor(null, 3840, 2160), new LinuxMonitors.Monitor("ASUS VS247", 1920, 1080)], monitors);
        }

        [Fact]
        public void AConnectorWithNoModesIsLeftOut()
        {
            Connector("card0-HDMI-A-1", "connected", "", Edid("X"));
            Assert.Empty(LinuxMonitors.Read(_root));
        }

        [Fact]
        public void NoDrmFolderIsNoMonitors() =>
            Assert.Empty(LinuxMonitors.Read(Path.Combine(_root, "missing")));

        [Theory]
        [InlineData("ASUS VS247", "ASUS VS247")]
        [InlineData("DELL S2725QS", "DELL S2725QS")]
        [InlineData(null, null)]
        public void TheEdidNameDescriptor(string? name, string? expected) =>
            Assert.Equal(expected, LinuxMonitors.EdidName(Edid(name)));

        [Fact]
        public void AShortEdidHasNoName() =>
            Assert.Null(LinuxMonitors.EdidName(new byte[10]));
    }
}
