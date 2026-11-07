using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using MangaJaNaiConverterGui.Services;
using MangaJaNaiConverterGui.ViewModels;
using Newtonsoft.Json;
using MangaJaNaiConverterGui.Drivers;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    public class PauseResumeTests
    {
        static (MainWindowViewModel Vm, UpscaleWorkflow Workflow, string Output) FolderRun(bool overwrite)
        {
            var root = Path.Combine(Path.GetTempPath(), "mangajanai-continue-" + Guid.NewGuid().ToString("N"));
            var input = Directory.CreateDirectory(Path.Combine(root, "in")).FullName;
            var output = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
            File.WriteAllBytes(Path.Combine(input, "a.png"), []);
            File.WriteAllBytes(Path.Combine(input, "b.png"), []);
            var backend = new FakeBackendService([]);
            var workflow = new UpscaleWorkflow
            {
                WorkflowName = "Test",
                SelectedTabIndex = 1,
                InputFolderPath = input,
                OutputFolderPath = output,
                OutputFilename = "%filename%",
                UpscaleImages = true,
                OverwriteExistingFiles = overwrite,
            };
            var vm = new MainWindowViewModel(backend) { Workflows = [workflow] };
            workflow.Vm = vm;
            workflow.Valid = true;
            return (vm, workflow, output);
        }

        // Continue counts what the CLI's --skip-written-since leaves to do: outputs written since the
        // interrupted run started are kept even when overwriting; older ones are redone.
        [AvaloniaFact]
        public void ContinueCountsOnlyWhatIsLeft()
        {
            var (vm, workflow, output) = FolderRun(overwrite: true);
            var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var before = Path.Combine(output, $"a.{workflow.ImageFormat}");
            var saved = Path.Combine(output, $"b.{workflow.ImageFormat}");
            File.WriteAllBytes(before, []);
            File.WriteAllBytes(saved, []);
            File.SetLastWriteTimeUtc(before, DateTimeOffset.FromUnixTimeSeconds(since - 100).UtcDateTime);
            File.SetLastWriteTimeUtc(saved, DateTimeOffset.FromUnixTimeSeconds(since + 10).UtcDateTime);

            vm.CheckInputs();
            Assert.Equal(2, vm.ProgressTotalFiles);
            vm.CheckInputs(since);
            Assert.Equal(1, vm.ProgressTotalFiles);

            workflow.OverwriteExistingFiles = false;
            vm.CheckInputs(since);
            Assert.Equal(0, vm.ProgressTotalFiles);
        }

        [AvaloniaFact]
        public void ContinueIsOfferedForTheWorkflowThatWasInterrupted()
        {
            var (vm, workflow, _) = FolderRun(overwrite: true);
            Assert.False(vm.ContinueEnabled);
            vm.InterruptedRunSince = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            vm.InterruptedRunWorkflow = 0;
            vm.InterruptedRunJob = MainWindowViewModel.JobOf(workflow);
            Assert.True(vm.ContinueEnabled);
            vm.InterruptedRunWorkflow = 1;
            Assert.False(vm.ContinueEnabled, "another workflow's run");
            vm.InterruptedRunWorkflow = 0;
            vm.Upscaling = true;
            Assert.False(vm.ContinueEnabled, "not while upscaling");
        }

        // Continue goes away once the workflow is set up for a different job.
        [AvaloniaFact]
        public void ContinueGoesAwayForADifferentJob()
        {
            var (vm, workflow, output) = FolderRun(overwrite: true);
            vm.InterruptedRunSince = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            vm.InterruptedRunJob = MainWindowViewModel.JobOf(workflow);
            Assert.True(vm.ContinueEnabled);

            var input = workflow.InputFolderPath;
            workflow.InputFolderPath = Directory.CreateDirectory(Path.Combine(output, "..", "other")).FullName;
            Assert.False(vm.ContinueEnabled, "another input folder");
            workflow.InputFolderPath = input + Path.DirectorySeparatorChar;
            Assert.True(vm.ContinueEnabled, "the same folder, written differently");

            workflow.OutputFolderPath = Path.Combine(output, "elsewhere");
            Assert.False(vm.ContinueEnabled, "another output folder");
            workflow.OutputFolderPath = output;
            workflow.SelectedTabIndex = 0;
            Assert.False(vm.ContinueEnabled, "a single file instead of the folder");
        }

        // A pause during an engine build waits for the build (the CLI checks between images), so it's
        // offered for the whole run. The status says when the GPU memory is actually free.
        [AvaloniaFact]
        public void PauseIsOfferedWhileUpscalingAndSaysWhenTheGpuIsFree()
        {
            var (vm, _, _) = FolderRun(overwrite: true);
            Assert.False(vm.PauseEnabled, "nothing running");
            vm.Upscaling = true;
            vm.EngineStatusText = "Building the TensorRT engine for x (a few minutes, once per model)...";
            Assert.True(vm.PauseEnabled);
            vm.Paused = true;
            Assert.StartsWith("Pausing", vm.LeftStatus);
            vm.PauseReleased = true;
            Assert.Contains("GPU memory is free", vm.LeftStatus);
        }

        // The interrupted run survives a restart: it's saved with the app state.
        [Fact]
        public void TheInterruptedRunIsSaved()
        {
            var vm = new MainWindowViewModel(new FakeBackendService([])) { InterruptedRunSince = 1762500000, InterruptedRunWorkflow = 2, InterruptedRunJob = "folder:c:\\in\nc:\\out" };
            var json = JsonConvert.SerializeObject(vm, NewtonsoftJsonSuspensionDriver.Settings);
            var back = JsonConvert.DeserializeObject<MainWindowViewModel>(json, NewtonsoftJsonSuspensionDriver.Settings)!;
            Assert.Equal(1762500000, back.InterruptedRunSince);
            Assert.Equal(2, back.InterruptedRunWorkflow);
            Assert.Equal("folder:c:\\in\nc:\\out", back.InterruptedRunJob);
        }
    }
}
