using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MangaJaNaiConverterGui.Services
{
    // Which NVIDIA GPUs TensorRT and TensorRT-RTX can run on. MangaJaNaiUpdater compiles this file
    // too (linked in its project), so the app and the updater share one rule.
    public static class GpuSupport
    {
        // The shipped TensorRT (11, on CUDA 13), TensorRT-RTX and libaji's CUDA kernels start at
        // compute capability 7.5: Turing, the RTX 20 / GTX 16 series. Older NVIDIA GPUs (GTX 10 series
        // and earlier, Volta) count as no NVIDIA GPU here: they upscale on DirectML. The PTX builder
        // pack can't help them, since PTX only compiles forward to newer GPUs.
        public static readonly (int Major, int Minor) TrtMinComputeCapability = (7, 5);

        public static bool TensorRtCapable(int major, int minor) =>
            (major, minor).CompareTo(TrtMinComputeCapability) >= 0;

        // From --list-devices' "major.minor". Unknown (null, an older backend, unparseable) counts as
        // capable, and the backend decides as before.
        public static bool TensorRtCapable(string? computeCapability) =>
            ParseComputeCapability(computeCapability) is not { } cc || TensorRtCapable(cc.Major, cc.Minor);

        public static (int Major, int Minor)? ParseComputeCapability(string? computeCapability)
        {
            var parts = (computeCapability ?? "").Trim().Split('.');
            return parts.Length == 2 &&
                   int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) &&
                   int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
                ? (major, minor)
                : null;
        }

        // "NVIDIA GeForce GTX 1080 (compute 6.1)", joined with " + " for several.
        public static string Describe(IEnumerable<(string Name, string ComputeCapability)> gpus) =>
            string.Join(" + ", gpus.Select(g => $"{g.Name} (compute {g.ComputeCapability})"));

        // Why TensorRT can't run when every NVIDIA GPU is too old for it (tooOld: Describe's text).
        public static string TooOldMessage(string tooOld, bool windows) =>
            $"TensorRT needs an NVIDIA GeForce RTX 20 / GTX 16 series or newer GPU (compute capability " +
            $"{TrtMinComputeCapability.Major}.{TrtMinComputeCapability.Minor}+), and {tooOld} is older. " +
            (windows
                ? "Choose DirectML in App Settings, which runs on this GPU."
                : "On Linux, MangaJaNai GUI runs on TensorRT only, so it can't upscale on this GPU.");

        // An NVIDIA GPU as NVML reports it (MangaJaNaiUpdater).
        public sealed record NvidiaGpu(string Name, int Major, int Minor)
        {
            public string Sm => $"sm{Major}{Minor}";
            public bool TensorRtCapable => GpuSupport.TensorRtCapable(Major, Minor);
        }

        // The NVIDIA GPUs TensorRT can use, as the updater reports them: whether there is one, their
        // generations ("sm86, sm120", each once) and names (" + "), and the GPUs left out as too old
        // (Describe's text, "" if none). A machine with only old GPUs has no NVIDIA GPU here.
        public static (bool HasNvidia, string Sm, string GpuName, string TooOld) Summarize(IEnumerable<NvidiaGpu> gpus)
        {
            var list = gpus.ToList();
            var usable = list.Where(g => g.TensorRtCapable).ToList();
            var tooOld = Describe(list.Where(g => !g.TensorRtCapable).Select(g => (g.Name, $"{g.Major}.{g.Minor}")));
            return usable.Count == 0
                ? (false, "", "", tooOld)
                : (true, string.Join(", ", usable.Select(g => g.Sm).Distinct()), string.Join(" + ", usable.Select(g => g.Name)), tooOld);
        }

        // The TensorRT builder packs for the GPU generations in sm ("sm86, sm120"): each one's own pack
        // if published, else the PTX fallback (a newer generation than the packs know).
        public static List<string> BuilderPacks(IEnumerable<string> published, string sm)
        {
            var names = published.ToHashSet();
            var packs = new List<string>();
            foreach (var s in sm.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pack = names.Contains($"trt-{s}") ? $"trt-{s}" : "trt-ptx";
                if (!packs.Contains(pack))
                {
                    packs.Add(pack);
                }
            }
            return packs;
        }
    }
}
