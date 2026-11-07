using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MangaJaNaiConverterGui.Services
{
    // The output file for each input, named the way mangajanai_upscale names them (MangaJaNaiCLI
    // run.rs `assign`), so the "already exist" counts match what the run will write. Inputs whose
    // outputs would land on the same file are told apart, and only those: first by adding
    // -%filename% to a name without it, then by adding the input's extension (a-zip.cbz), then by
    // a number.
    public static class OutputNames
    {
        public static string[] Assign(IReadOnlyList<(string Source, string Directory, string Extension)> inputs, string template)
        {
            if (string.IsNullOrEmpty(template))
            {
                template = "%filename%";
            }
            var withName = template.Contains("%filename%") ? template : template + "-%filename%";
            var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

            string Render(string t, int i) => Path.Join(inputs[i].Directory, t.Replace("%filename%", Path.GetFileNameWithoutExtension(inputs[i].Source)) + "." + inputs[i].Extension);

            bool[] Colliding(string[] dests)
            {
                var counts = dests.GroupBy(d => d, comparer).ToDictionary(g => g.Key, g => g.Count(), comparer);
                return dests.Select(d => counts[d] > 1).ToArray();
            }

            var dests = Enumerable.Range(0, inputs.Count).Select(i => Render(template, i)).ToArray();

            var c = Colliding(dests);
            for (var i = 0; i < dests.Length; i++)
            {
                if (c[i])
                {
                    dests[i] = Render(withName, i);
                }
            }

            c = Colliding(dests);
            for (var i = 0; i < dests.Length; i++)
            {
                if (c[i])
                {
                    var sourceExt = Path.GetExtension(inputs[i].Source).TrimStart('.').ToLowerInvariant();
                    dests[i] = Render(withName + "-" + sourceExt, i);
                }
            }

            var used = new HashSet<string>(comparer);
            for (var i = 0; i < dests.Length; i++)
            {
                var stem = Path.Join(Path.GetDirectoryName(dests[i]), Path.GetFileNameWithoutExtension(dests[i]));
                var ext = Path.GetExtension(dests[i]);
                var candidate = dests[i];
                for (var n = 2; !used.Add(candidate); n++)
                {
                    candidate = $"{stem}-{n}{ext}";
                }
                dests[i] = candidate;
            }

            return dests;
        }
    }
}
