using MangaJaNaiConverterGui.Drivers;
using MangaJaNaiConverterGui.ViewModels;
using Newtonsoft.Json;
using System.Linq;
using System.Reflection;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    public class ReaderDeviceTests
    {
        static readonly ReaderDevice[] Devices = ReaderDevice.Parse("""
            [
              {"Name": "Elipsa 2E", "Brand": "Kobo", "Year": "2023", "Width": 1404, "Height": 1872, "Popular": true},
              {"Name": "Obscure Tab 8", "Brand": "Blackview", "Year": "2025", "Width": 800, "Height": 1280},
              {"Name": "iPad Pro 12.9-inch", "Brand": "Apple", "Year": "2015-2022", "Width": 2048, "Height": 2732},
              {"Name": "iPad Pro 13-inch", "Brand": "Apple", "Year": "2024", "Width": 2064, "Height": 2752, "Popular": true},
              {"Name": "PC Monitor 4K UHD", "Brand": "Generic", "Year": "", "Width": 2160, "Height": 3840, "Popular": true},
              {"Name": "Galaxy Tab S9 Ultra", "Brand": "Samsung", "Year": "2023", "Width": 1848, "Height": 2960, "Popular": true},
              {"Name": "Galaxy Tab S9 FE", "Brand": "Samsung", "Year": "2023", "Width": 1440, "Height": 2304},
              {"Name": "Galaxy Tab S9", "Brand": "Samsung", "Year": "2023", "Width": 1600, "Height": 2560},
              {"Name": "Galaxy Tab A7 Lite", "Brand": "Samsung", "Year": "2021", "Width": 800, "Height": 1340},
              {"Name": "No Size", "Brand": "Broken", "Year": "2024", "Width": 0, "Height": 0}
            ]
            """).ToArray();

        static string[] Labels(string? text) => ReaderDevice.Search(Devices, text).Select(d => d.ToString()).ToArray();

        [Fact]
        public void DevicesWithoutASizeAreDropped() =>
            Assert.DoesNotContain(Devices, d => d.Brand == "Broken");

        [Fact]
        public void NothingTypedShowsThePopularDevicesNewestFirst() =>
            Assert.Equal(["Apple iPad Pro 13-inch (2024)", "Kobo Elipsa 2E (2023)", "Samsung Galaxy Tab S9 Ultra (2023)", "Generic PC Monitor 4K UHD"], Labels(""));

        [Fact]
        public void NothingTypedShowsTheDevicesAlreadyPickedFirst() =>
            Assert.Equal(["Blackview Obscure Tab 8 (2025)", "Apple iPad Pro 13-inch (2024)"],
                ReaderDevice.Search(Devices, "", ["Blackview Obscure Tab 8 (2025)", null, "1404 x 1872"]).Take(2).Select(d => d.ToString()));

        // The device spelled out comes first, even when popular ones also match.
        [Fact]
        public void TheDeviceSpelledOutBeatsPopularOnes() =>
            Assert.Equal("Samsung Galaxy Tab S9 (2023)", Labels("galaxy tab s9")[0]);

        [Fact]
        public void AnObscureDeviceSpelledOutComesFirst() =>
            Assert.Equal("Samsung Galaxy Tab A7 Lite (2021)", Labels("tab a7 lite")[0]);

        [Fact]
        public void PartsOfWordsStillMatch() =>
            Assert.Equal(["Apple iPad Pro 12.9-inch (2015-2022)"], Labels("pad 12"));

        // A whole word beats the start of one: "s9" is all of "S9", only the start of "S9FE".
        [Fact]
        public void WholeWordsBeatTheStartOfOne()
        {
            var devices = ReaderDevice.Parse("""
                [{"Name": "Tab S9FE", "Brand": "X", "Year": "2025", "Width": 1, "Height": 2, "Popular": true},
                 {"Name": "Tab S9 Max", "Brand": "X", "Year": "2020", "Width": 1, "Height": 2}]
                """);
            Assert.Equal("X Tab S9 Max (2020)", ReaderDevice.Search(devices, "s9")[0].ToString());
        }

        // Only the brand typed: nothing is spelled out, so popular ones lead, then the newest.
        [Fact]
        public void ABrandAloneListsPopularThenNewest() =>
            Assert.Equal(["Samsung Galaxy Tab S9 Ultra (2023)", "Samsung Galaxy Tab S9 FE (2023)", "Samsung Galaxy Tab S9 (2023)", "Samsung Galaxy Tab A7 Lite (2021)"], Labels("samsung"));

        // Words left out, in any order, with the brand instead of the line: the plain S9 still leads.
        [Theory]
        [InlineData("galaxy s9")]
        [InlineData("samsung s9")]
        [InlineData("s9 samsung")]
        [InlineData("S9")]
        public void ALooseModelSearchFindsTheClosestModel(string text) =>
            Assert.Equal("Samsung Galaxy Tab S9 (2023)", Labels(text)[0]);

        [Theory]
        [InlineData("s9 plus")]
        [InlineData("s9+")]
        [InlineData("S9 +")]
        public void PlusAndTheWordPlusAreTheSame(string text)
        {
            var devices = ReaderDevice.Parse("""
                [{"Name": "Galaxy Tab S9+", "Brand": "Samsung", "Year": "2023", "Width": 1752, "Height": 2800},
                 {"Name": "Galaxy Tab S9", "Brand": "Samsung", "Year": "2023", "Width": 1600, "Height": 2560}]
                """);
            Assert.Equal("Samsung Galaxy Tab S9+ (2023)", ReaderDevice.Search(devices, text)[0].ToString());
        }

        [Theory]
        [InlineData("elipsa2e")]
        [InlineData("koboelipsa")]
        [InlineData("elipsa 2e kobo 2023")]
        [InlineData("Elipsa-2E")]
        public void SpacingAndPunctuationDontMatter(string text) =>
            Assert.Equal(["Kobo Elipsa 2E (2023)"], Labels(text));

        [Theory]
        [InlineData("paperwite")]
        [InlineData("paperwhtie")]
        [InlineData("paperrwhite")]
        public void AOneLetterTypoInALongWordStillMatches(string text)
        {
            var devices = ReaderDevice.Parse("""[{"Name": "Kindle Paperwhite", "Brand": "Amazon", "Year": "2024", "Width": 1264, "Height": 1680}]""");
            Assert.Single(ReaderDevice.Search(devices, text));
        }

        // "s8" isn't a typo for "s9": short words are model numbers.
        [Fact]
        public void ShortWordsMustMatchExactly() =>
            Assert.Empty(Labels("galaxy s8"));

        // "Air5" reads as "Air 5": the popular Note Air5 C isn't ranked below an older "Note Air 2" for
        // being glued together, and "air 5", "air5" both find it. Short codes like "S9" stay whole.
        [Theory]
        [InlineData("note air", "ONYX Boox Note Air5 C (2025)")]
        [InlineData("note air 5", "ONYX Boox Note Air5 C (2025)")]
        [InlineData("note air5", "ONYX Boox Note Air5 C (2025)")]
        [InlineData("fold 7", "Samsung Galaxy Z Fold7 (2025)")]
        [InlineData("fold7", "Samsung Galaxy Z Fold7 (2025)")]
        public void AWordGluedToItsNumberMatchesEitherWay(string text, string first)
        {
            var devices = ReaderDevice.Parse("""
                [{"Name": "Boox Note Air 2", "Brand": "ONYX", "Year": "2021", "Width": 1404, "Height": 1872},
                 {"Name": "Boox Note Air5 C", "Brand": "ONYX", "Year": "2025", "Width": 1860, "Height": 2480, "Popular": true},
                 {"Name": "Galaxy Z Fold7", "Brand": "Samsung", "Year": "2025", "Width": 1968, "Height": 2184},
                 {"Name": "Galaxy Z Fold6", "Brand": "Samsung", "Year": "2024", "Width": 1856, "Height": 2160}]
                """);
            Assert.Equal(first, ReaderDevice.Search(devices, text)[0].ToString());
        }

        [Fact]
        public void TheInchWordIsOptional() =>
            Assert.Equal("Apple iPad Pro 13-inch (2024)", Labels("ipad pro 13")[0]);

        [Fact]
        public void PickingADeviceFindsItFirst() =>
            Assert.Equal("Samsung Galaxy Tab S9 FE (2023)", Labels("Samsung Galaxy Tab S9 FE (2023)")[0]);

        [Fact]
        public void SearchWordsMatchInAnyOrderAndCase() =>
            Assert.Equal(["Kobo Elipsa 2E (2023)"], Labels("elipsa KOBO"));

        [Theory]
        [InlineData("2015-2022", 2022)]
        [InlineData("2024", 2024)]
        [InlineData("", 0)]
        public void AYearRangeSortsByItsLastYear(string year, int latest) =>
            Assert.Equal(latest, new ReaderDevice { Year = year }.LatestYear);

        [Fact]
        public void AnEmptyListWithNoPopularDevicesShowsThemAll() =>
            Assert.Single(ReaderDevice.Search([Devices[1]], ""));

        [Theory]
        [InlineData("Kobo Elipsa 2E (2023)", 1404, 1872)]
        [InlineData("  kobo elipsa 2e (2023) ", 1404, 1872)]
        [InlineData("1404 x 1872", 1404, 1872)]
        [InlineData("1872×1404", 1872, 1404)]
        [InlineData("1404x1872", 1404, 1872)]
        [InlineData("1404X1872", 1404, 1872)]
        [InlineData("1404*1872", 1404, 1872)]
        [InlineData("1404 1872", 1404, 1872)]
        [InlineData("1404,1872", 1404, 1872)]
        [InlineData("1404, 1872", 1404, 1872)]
        [InlineData("1404 by 1872", 1404, 1872)]
        [InlineData("My monitor (2560 × 1440)", 2560, 1440)]
        public void SizeOfReadsListedDevicesAndTypedSizes(string text, int a, int b) =>
            Assert.Equal((a, b), ReaderDevice.SizeOf(Devices, text));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Kobo Elipsa")]
        [InlineData("1404")]
        [InlineData("0 x 1872")]
        [InlineData("fire hd 10 2023")]
        [InlineData("tab 2024 2025")]
        [InlineData("10 2023")]
        public void SizeOfAnythingElseIsNull(string? text) =>
            Assert.Null(ReaderDevice.SizeOf(Devices, text));

        // ---- the bundled list -------------------------------------------------------------------------

        [Fact]
        public void TheBundledListIsValid()
        {
            var all = ReaderDevice.All;
            Assert.NotEmpty(all);
            Assert.All(all, d =>
            {
                Assert.True(d.Width <= d.Height, $"{d}: Width is the short side");
                Assert.Equal(d.Name, d.Name.Trim());
                Assert.Equal(d.Brand, d.Brand.Trim());
            });
            var duplicates = all.GroupBy(d => d.ToString()).Where(g => g.Count() > 1).Select(g => g.Key);
            Assert.Empty(duplicates);
            Assert.Contains(all, d => d.Popular);
        }

        // The reset workflow's device must be in the list, or Fit to Display starts out invalid.
        [Fact]
        public void TheDefaultWorkflowsDeviceIsListed()
        {
            var json = (string)typeof(MainWindowViewModel).GetField("DEFAULT_WORKFLOW", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var workflow = JsonConvert.DeserializeObject<UpscaleWorkflow>(json, NewtonsoftJsonSuspensionDriver.Settings)!;
            Assert.Equal((1404, 1872), (workflow.DisplayDeviceWidth, workflow.DisplayDeviceHeight));
        }

        // The shipped workflows' devices too (it names them by label, as the old server did).
        [Fact]
        public void TheDefaultStatesDevicesAreListed()
        {
            var names = System.Text.RegularExpressions.Regex.Matches(System.IO.File.ReadAllText(NewtonsoftJsonSuspensionDriver.DefaultStatePath), "\"DisplayDevice\": \"([^\"]*)\"")
                .Select(m => m.Groups[1].Value).ToList();
            Assert.NotEmpty(names);
            Assert.All(names, n => Assert.NotNull(ReaderDevice.SizeOf(n)));
        }

        // ---- the workflow's display size ---------------------------------------------------------------

        [Fact]
        public void OrientationPicksWhichSideIsTheWidth()
        {
            var workflow = new UpscaleWorkflow { DisplayDevice = "1872 x 1404" };
            Assert.Equal((1404, 1872), (workflow.DisplayDeviceWidth, workflow.DisplayDeviceHeight));
            workflow.DisplayPortraitSelected = false;
            Assert.Equal((1872, 1404), (workflow.DisplayDeviceWidth, workflow.DisplayDeviceHeight));
        }


        [Fact]
        public void AnUnknownNameHasNoSize() =>
            Assert.Equal(0, new UpscaleWorkflow { DisplayDevice = "Some Tablet" }.DisplayDeviceWidth);

        // ---- connected monitors -----------------------------------------------------------------------

        static readonly ReaderDevice MainMonitor = ReaderDevice.ConnectedMonitor("27M2V", true, 3840, 2160);
        static readonly ReaderDevice OtherMonitor = ReaderDevice.ConnectedMonitor("DELL S2725QS", false, 2160, 3840);

        [Fact]
        public void AConnectedMonitorIsLabeledByWhatPeopleRecognize()
        {
            Assert.Equal("Your main monitor · 3840 × 2160 landscape · 27M2V", MainMonitor.ToString());
            Assert.Equal("Your other monitor · 2160 × 3840 portrait · DELL S2725QS", OtherMonitor.ToString());
            Assert.Equal("Your other monitor · 1920 × 1080 landscape", ReaderDevice.ConnectedMonitor(" ", false, 1920, 1080).ToString());
        }

        // Listed like any device: in the empty list after the devices with a year, not pinned first.
        [Fact]
        public void ConnectedMonitorsAreNotPinnedFirst()
        {
            var shown = ReaderDevice.Search([.. Devices, MainMonitor, OtherMonitor], "").Select(d => d.ToString()).ToList();
            Assert.Equal("Apple iPad Pro 13-inch (2024)", shown[0]);
            Assert.Equal([MainMonitor.ToString(), OtherMonitor.ToString()], shown.TakeLast(2));
        }

        [Theory]
        [InlineData("main monitor", "Your main monitor · 3840 × 2160 landscape · 27M2V")]
        [InlineData("dell", "Your other monitor · 2160 × 3840 portrait · DELL S2725QS")]
        [InlineData("monitor portrait", "Your other monitor · 2160 × 3840 portrait · DELL S2725QS")]
        public void ConnectedMonitorsAreFoundLikeAnyDevice(string text, string first) =>
            Assert.Equal(first, ReaderDevice.Search([.. Devices, MainMonitor, OtherMonitor], text)[0].ToString());

        [Fact]
        public void PickingAConnectedMonitorMatchesItsRotation()
        {
            var workflow = new UpscaleWorkflow { DisplayDevice = MainMonitor.ToString() };
            Assert.False(workflow.DisplayPortraitSelected);
            Assert.Equal((3840, 2160), (workflow.DisplayDeviceWidth, workflow.DisplayDeviceHeight));
            workflow.DisplayDevice = OtherMonitor.ToString();
            Assert.True(workflow.DisplayPortraitSelected);
            Assert.Equal((2160, 3840), (workflow.DisplayDeviceWidth, workflow.DisplayDeviceHeight));
        }

        // The orientation the user chose afterwards wins on load, and the size outlives the monitor.
        [Fact]
        public void ASavedConnectedMonitorKeepsItsSizeAndChosenOrientation()
        {
            var workflow = new UpscaleWorkflow { DisplayDevice = OtherMonitor.ToString() };
            workflow.DisplayPortraitSelected = false;
            var back = JsonConvert.DeserializeObject<UpscaleWorkflow>(Reload(workflow), NewtonsoftJsonSuspensionDriver.Settings)!;
            Assert.False(back.DisplayPortraitSelected);
            Assert.Equal((3840, 2160), (back.DisplayDeviceWidth, back.DisplayDeviceHeight));
        }

        // A device the list no longer has keeps the size it was saved with (the old server's devices).
        [Fact]
        public void ASavedSizeSurvivesForADeviceNoLongerListed()
        {
            var saved = Reload(new UpscaleWorkflow { DisplayDevice = "1650 x 2200" })
                .Replace("\"DisplayDevice\": \"1650 x 2200\"", "\"DisplayDevice\": \"ONYX Retired Model (2021)\"");
            var back = JsonConvert.DeserializeObject<UpscaleWorkflow>(saved, NewtonsoftJsonSuspensionDriver.Settings)!;
            Assert.Equal((1650, 2200), (back.DisplayDeviceWidth, back.DisplayDeviceHeight));
        }

        // Saved landscape: the sides come back right whichever order they load in.
        [Fact]
        public void ASavedLandscapeSizeRoundTrips()
        {
            var workflow = new UpscaleWorkflow { DisplayDevice = "1404 x 1872", DisplayPortraitSelected = false };
            var back = JsonConvert.DeserializeObject<UpscaleWorkflow>(Reload(workflow), NewtonsoftJsonSuspensionDriver.Settings)!;
            Assert.False(back.DisplayPortraitSelected);
            Assert.Equal((1872, 1404), (back.DisplayDeviceWidth, back.DisplayDeviceHeight));
        }

        // States saved before the bundled list held the server's answers in DisplayDeviceMap.
        [Fact]
        public void AStateWithTheOldDeviceMapStillLoads()
        {
            var json = """
                {
                  "$type": "MangaJaNaiConverterGui.ViewModels.MainWindowViewModel, MangaJaNaiConverterGui",
                  "DisplayDeviceMap": {
                    "$type": "Avalonia.Collections.AvaloniaDictionary`2[[System.String, System.Private.CoreLib],[MangaJaNaiConverterGui.ViewModels.ReaderDevice, MangaJaNaiConverterGui]], Avalonia.Base",
                    "Kobo Elipsa 2E (2023)": {"$type": "MangaJaNaiConverterGui.ViewModels.ReaderDevice, MangaJaNaiConverterGui", "Name": "Elipsa 2E", "Brand": "Kobo", "Year": "2023", "Width": 1404, "Height": 1872}
                  },
                  "AutoUpdateEnabled": true
                }
                """;
            Assert.NotNull(JsonConvert.DeserializeObject<object>(json, NewtonsoftJsonSuspensionDriver.Settings) as MainWindowViewModel);
        }

        static string Reload(UpscaleWorkflow workflow) => JsonConvert.SerializeObject(workflow, NewtonsoftJsonSuspensionDriver.Settings);
    }
}
