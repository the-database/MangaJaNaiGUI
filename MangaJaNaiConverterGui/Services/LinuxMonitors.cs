using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MangaJaNaiConverterGui.Services
{
    // The monitors connected to a Linux machine, read from the kernel (/sys/class/drm), not from the
    // display server: Avalonia goes through X11/XWayland, which names monitors by connector ("HDMI-2")
    // and, with Wayland scaling, can report a scaled size rather than the panel's. The kernel's preferred
    // mode is the panel's native resolution, and the EDID carries the model name. No root needed.
    public static partial class LinuxMonitors
    {
        public record Monitor(string? Name, int Width, int Height);

        public static IReadOnlyList<Monitor> Read(string drmRoot = "/sys/class/drm")
        {
            var monitors = new List<Monitor>();
            try
            {
                foreach (var connector in Directory.GetDirectories(drmRoot, "card*-*").Order(StringComparer.Ordinal))
                {
                    try
                    {
                        if (File.ReadAllText(Path.Combine(connector, "status")).Trim() != "connected")
                        {
                            continue;
                        }

                        // The kernel lists the preferred (native) mode first: "1920x1080".
                        var first = File.ReadLines(Path.Combine(connector, "modes")).FirstOrDefault();
                        var mode = first == null ? null : ModePattern().Match(first);
                        if (mode is not { Success: true })
                        {
                            continue;
                        }

                        var edidPath = Path.Combine(connector, "edid");
                        var name = File.Exists(edidPath) ? EdidName(File.ReadAllBytes(edidPath)) : null;
                        monitors.Add(new Monitor(name, int.Parse(mode.Groups[1].Value), int.Parse(mode.Groups[2].Value)));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // A connector that can't be read is left out rather than guessed at.
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return [];
            }

            return monitors.Distinct().ToList();
        }

        // The EDID's monitor name descriptor (tag 0xFC), e.g. "ASUS VS247"; null if it has none.
        public static string? EdidName(byte[] edid)
        {
            if (edid.Length < 128)
            {
                return null;
            }

            for (var i = 54; i <= 108; i += 18)
            {
                if (edid[i] == 0 && edid[i + 1] == 0 && edid[i + 3] == 0xFC)
                {
                    var text = Encoding.ASCII.GetString(edid, i + 5, 13);
                    var end = text.IndexOf('\n');
                    var name = (end >= 0 ? text[..end] : text).Trim();
                    return name.Length > 0 ? name : null;
                }
            }

            return null;
        }

        [GeneratedRegex(@"^(\d+)x(\d+)")]
        private static partial Regex ModePattern();
    }
}
