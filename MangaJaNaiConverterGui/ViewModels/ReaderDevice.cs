using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MangaJaNaiConverterGui.ViewModels
{
    // A tablet, e-reader or monitor from devices.json. Width is the short side, Height the long one.
    public partial class ReaderDevice
    {
        public string Name { get; set; } = default!;
        public string Brand { get; set; } = default!;
        public string Year { get; set; } = default!;
        public int Width { get; set; } = default!;
        public int Height { get; set; } = default!;

        // Shown before anything is typed, and first among search results.
        public bool Popular { get; set; }

        // The newest year in Year ("2015-2022" is 2022), or 0 for none.
        public int LatestYear =>
            YearPattern().Matches(Year ?? "").Select(m => int.Parse(m.Value)).DefaultIfEmpty(0).Max();

        public override string ToString()
        {
            List<string> parts = [];

            if (!string.IsNullOrWhiteSpace(Brand))
            {
                parts.Add(Brand);
            }

            if (!string.IsNullOrWhiteSpace(Name))
            {
                parts.Add(Name);
            }

            if (!string.IsNullOrWhiteSpace(Year))
            {
                parts.Add($"({Year})");
            }

            return string.Join(" ", parts);
        }

        // The list ships inside the app (devices.json, an embedded resource): no server to keep up.
        private static readonly Lazy<IReadOnlyList<ReaderDevice>> _all = new(() =>
        {
            using var stream = typeof(ReaderDevice).Assembly.GetManifestResourceStream("MangaJaNaiConverterGui.devices.json");
            return stream == null ? [] : Parse(new StreamReader(stream).ReadToEnd());
        });

        public static IReadOnlyList<ReaderDevice> All => _all.Value;

        // The devices in a devices.json, without any that have no size.
        public static IReadOnlyList<ReaderDevice> Parse(string json)
        {
            try
            {
                return (JsonConvert.DeserializeObject<List<ReaderDevice>>(json) ?? [])
                    .Where(d => d != null && d.Width > 0 && d.Height > 0)
                    .ToList();
            }
            catch (JsonException)
            {
                return [];
            }
        }

        // Nothing typed: the devices this user already picked (recent), then the popular ones.
        // Typed: every device whose label has every word typed, in any order and with words left out
        // ("samsung s9" finds the Galaxy Tab S9), ranked by how well it matches:
        //   1. the whole name spelled out ("galaxy tab s9" over a popular S9 Ultra)
        //   2. match quality: whole words, then starts of words, then the rest
        //   3. with a model number typed, the closest model: fewest name words left over, so
        //      "galaxy s9" is the S9 before the S9 FE or S9 Ultra
        //   4. popular, then newest; these only break ties, which is what a brand alone gives.
        // Nothing is cut off; the list scrolls.
        public static List<ReaderDevice> Search(IEnumerable<ReaderDevice> devices, string? text, IEnumerable<string?>? recent = null)
        {
            var words = Tokens(text);
            var list = devices.ToList();

            if (words.Count == 0)
            {
                var mine = new HashSet<string>((recent ?? []).OfType<string>().Select(r => r.Trim()), StringComparer.OrdinalIgnoreCase);
                var shown = list.Where(d => d.Popular || mine.Contains(d.ToString())).ToList();
                return (shown.Count > 0 ? shown : list)
                    .OrderByDescending(d => mine.Contains(d.ToString()))
                    .ThenByDescending(d => d.Popular)
                    .ThenByDescending(d => d.LatestYear)
                    .ToList();
            }

            var modelNumberTyped = words.Any(w => w.Any(char.IsDigit) && !YearPattern().IsMatch(w));
            return list
                .Select(d => (Device: d, Match: Match(d, words)))
                .Where(x => x.Match != null)
                .OrderByDescending(x => x.Match!.Value.Complete)
                .ThenByDescending(x => x.Match!.Value.Quality)
                .ThenBy(x => modelNumberTyped ? x.Match!.Value.Leftover : 0)
                .ThenByDescending(x => x.Device.Popular)
                .ThenByDescending(x => x.Device.LatestYear)
                .Select(x => x.Device)
                .ToList();
        }

        // Lowercase words, split at anything but letters, digits and decimal points ("12.9-inch" is
        // "12.9" and "inch"), with "+" as the word "plus" ("S9+" is "s9 plus", like typing it out),
        // and a word glued to its number split off it ("Air5" is "air 5", like "Air 2"; "Fold7" is
        // "fold 7"). Short model codes stay whole: "S9", "M4", "A16".
        private static List<string> Tokens(string? text) =>
            TokenSplit().Split(GluedNumber().Replace((text ?? "").ToLowerInvariant().Replace("+", " plus "), "$1 $2"))
                .Select(t => t.Trim('.'))
                .Where(t => t.Length > 0)
                .ToList();

        // Letters and digits only, so words typed together still match: "elipsa2e", "galaxytab".
        private static string Squashed(string text) => string.Concat(Tokens(text));

        // A device's words, worked out once rather than on every keystroke (listed devices don't change).
        private (List<string> Label, string Squashed, List<string> Name)? _searchWords;

        private (List<string> Label, string Squashed, List<string> Name) SearchWords() =>
            _searchWords ??= (Tokens(ToString()), Squashed(ToString()), Tokens(Name).Where(n => !Filler.Contains(n)).ToList());

        // Words that don't tell models apart, so leaving them out still spells out the name.
        private static readonly HashSet<string> Filler = ["inch", "generation", "gen", "edition"];

        // How well the typed words match: null if one doesn't appear at all. Quality adds up, per word,
        // 3 for a whole word ("s9"), 2 for the start of one ("paper" in "Paperwhite"), 1 for inside one,
        // a one-letter typo ("paperwite") or words typed together. Complete: every word of the name
        // (not the brand or year) was typed. Leftover: the name's words that weren't.
        private static (bool Complete, int Quality, int Leftover)? Match(ReaderDevice device, List<string> words)
        {
            var (labelWords, squashed, nameWords) = device.SearchWords();
            var quality = 0;
            foreach (var w in words)
            {
                var best = labelWords.Max(l =>
                    l == w ? 3 :
                    l.StartsWith(w, StringComparison.Ordinal) ? 2 :
                    l.Contains(w, StringComparison.Ordinal) || IsTypo(l, w) ? 1 : 0);
                if (best == 0 && squashed.Contains(w, StringComparison.Ordinal))
                {
                    best = 1;
                }
                if (best == 0)
                {
                    return null;
                }
                quality += best;
            }

            var leftover = nameWords.Count(n => !words.Any(w => w == n || IsTypo(n, w)));
            return (nameWords.Count > 0 && leftover == 0, quality, leftover);
        }

        // One letter added, dropped, changed or swapped with the next, in words long enough for that to be a typo, not
        // another model ("s9" vs "s8" are different tablets).
        private static bool IsTypo(string word, string typed)
        {
            if (typed.Length < 5 || word.Length < 5 || Math.Abs(word.Length - typed.Length) > 1 || word == typed)
            {
                return false;
            }

            int i = 0, j = 0, edits = 0;
            while (i < word.Length && j < typed.Length)
            {
                if (word[i] == typed[j])
                {
                    i++;
                    j++;
                    continue;
                }
                if (++edits > 1)
                {
                    return false;
                }
                // Two letters swapped ("paperwhtie") count as one typo.
                if (word.Length == typed.Length && i + 1 < word.Length && word[i] == typed[j + 1] && word[i + 1] == typed[j])
                {
                    i += 2;
                    j += 2;
                }
                else if (word.Length > typed.Length) i++;
                else if (word.Length < typed.Length) j++;
                else { i++; j++; }
            }
            return edits + (word.Length - i) + (typed.Length - j) <= 1;
        }

        // The size behind what's in the device box: a listed device's label, or a typed size such as
        // "1404 x 1872" or "This display (2560 × 1440)". The two sides come back in the order given.
        public static (int, int)? SizeOf(IEnumerable<ReaderDevice> devices, string? text)
        {
            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return null;
            }

            var device = devices.FirstOrDefault(d => string.Equals(d.ToString(), trimmed, StringComparison.OrdinalIgnoreCase));
            if (device != null)
            {
                return (device.Width, device.Height);
            }

            var match = SizePattern().Match(trimmed);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var a) && int.TryParse(match.Groups[2].Value, out var b) && a > 0 && b > 0)
            {
                return (a, b);
            }

            return null;
        }

        public static (int, int)? SizeOf(string? text) => SizeOf(All, text);

        // The label "Use this display" gives a monitor, which SizeOf reads back.
        public static string DisplayLabel(int width, int height) => $"This display ({width} × {height})";

        [GeneratedRegex(@"(?<!\d)(\d{2,5})\s*[x×X*]\s*(\d{2,5})(?!\d)")]
        private static partial Regex SizePattern();

        [GeneratedRegex(@"[^\p{L}\p{N}.]+")]
        private static partial Regex TokenSplit();

        [GeneratedRegex(@"(\p{L}{3,})(\p{N})")]
        private static partial Regex GluedNumber();

        [GeneratedRegex(@"(?<!\d)(19|20)\d{2}(?!\d)")]
        private static partial Regex YearPattern();
    }
}
