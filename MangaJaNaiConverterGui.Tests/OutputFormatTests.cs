using MangaJaNaiConverterGui.Drivers;
using MangaJaNaiConverterGui.ViewModels;
using Newtonsoft.Json;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    public class OutputFormatTests
    {
        [Fact]
        public void JpegXlIsExclusiveAndOffersLosslessAndQuality()
        {
            var w = new UpscaleWorkflow();
            w.SetJxlSelected();

            Assert.True(w.JxlSelected);
            Assert.False(w.WebpSelected || w.PngSelected || w.JpegSelected || w.AvifSelected);
            Assert.Equal("jxl", w.ImageFormat);
            Assert.True(w.ShowUseLosslessCompression);
            Assert.True(w.ShowLossyCompressionQuality);

            w.UseLosslessCompression = true;
            Assert.False(w.ShowLossyCompressionQuality);

            w.SetAvifSelected();
            Assert.False(w.JxlSelected);
            Assert.Equal("avif", w.ImageFormat);
            Assert.False(w.ShowUseLosslessCompression);
        }

        // The CLI picks the format from these flags in the saved state, so JxlSelected must round-trip;
        // a workflow saved before it existed reads as not JPEG XL.
        [Fact]
        public void JxlSelectedIsSavedAndOldWorkflowsReadAsBefore()
        {
            var w = new UpscaleWorkflow();
            w.SetJxlSelected();
            var json = JsonConvert.SerializeObject(w, NewtonsoftJsonSuspensionDriver.Settings);
            Assert.Contains("\"JxlSelected\": true", json);

            var back = (UpscaleWorkflow)JsonConvert.DeserializeObject(json, NewtonsoftJsonSuspensionDriver.Settings)!;
            Assert.True(back.JxlSelected);
            Assert.Equal("jxl", back.ImageFormat);

            var old = (UpscaleWorkflow)JsonConvert.DeserializeObject(json.Replace("\"JxlSelected\": true,", "").Replace("\"WebpSelected\": false", "\"WebpSelected\": true"), NewtonsoftJsonSuspensionDriver.Settings)!;
            Assert.False(old.JxlSelected);
            Assert.Equal("webp", old.ImageFormat);
        }

        [Fact]
        public void JpegXlOffersDistanceInPlaceOfQuality()
        {
            var w = new UpscaleWorkflow();
            Assert.False(w.ShowJxlQualityMode);
            Assert.True(w.ShowQualityValue);

            w.SetJxlSelected();
            Assert.True(w.ShowJxlQualityMode);
            Assert.True(w.ShowQualityValue);
            Assert.False(w.ShowJxlDistanceValue);
            Assert.Equal("JPEG XL q80", w.OutputSummary);

            w.JxlQualityModeIndex = 1;
            Assert.True(w.UseJxlDistance);
            Assert.False(w.ShowQualityValue);
            Assert.True(w.ShowJxlDistanceValue);
            w.JxlDistance = 0.5;
            Assert.Equal("JPEG XL d0.5", w.OutputSummary);

            w.UseLosslessCompression = true;
            Assert.False(w.ShowJxlQualityMode || w.ShowQualityValue || w.ShowJxlDistanceValue);
            Assert.Equal("JPEG XL lossless", w.OutputSummary);

            w.UseLosslessCompression = false;
            w.SetAvifSelected();
            Assert.True(w.ShowQualityValue, "other formats keep the quality");
            Assert.False(w.ShowJxlQualityMode || w.ShowJxlDistanceValue);
        }

        // The CLI reads UseJxlDistance and JxlDistance from the saved state; a workflow saved before
        // they existed uses the quality.
        [Fact]
        public void JxlDistanceIsSavedAndOldWorkflowsUseTheQuality()
        {
            var w = new UpscaleWorkflow();
            w.SetJxlSelected();
            w.UseJxlDistance = true;
            w.JxlDistance = 0.5;
            var json = JsonConvert.SerializeObject(w, NewtonsoftJsonSuspensionDriver.Settings);
            Assert.Contains("\"UseJxlDistance\": true", json);
            Assert.Contains("\"JxlDistance\": 0.5", json);

            var back = (UpscaleWorkflow)JsonConvert.DeserializeObject(json, NewtonsoftJsonSuspensionDriver.Settings)!;
            Assert.True(back.UseJxlDistance);
            Assert.Equal(0.5, back.JxlDistance);

            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            obj.Remove("UseJxlDistance");
            obj.Remove("JxlDistance");
            var old = (UpscaleWorkflow)JsonConvert.DeserializeObject(obj.ToString(), NewtonsoftJsonSuspensionDriver.Settings)!;
            Assert.False(old.UseJxlDistance);
            Assert.Equal(1.0, old.JxlDistance);
            Assert.True(old.ShowQualityValue);
        }

        [Theory]
        [InlineData("page.jxl")]
        [InlineData("page.TIFF")]
        [InlineData("page.tif")]
        public void TheCliImageTypesCountAsImages(string name) =>
            Assert.Contains(MainWindowViewModel.IMAGE_EXTENSIONS, x => name.ToLower().EndsWith(x));

        [Theory]
        [InlineData("vol.7z")]
        [InlineData("vol.cb7")]
        public void TheCliArchiveTypesCountAsArchives(string name) =>
            Assert.Contains(MainWindowViewModel.ARCHIVE_EXTENSIONS, x => name.ToLower().EndsWith(x));
    }
}
