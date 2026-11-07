using System;
using System.Diagnostics;
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
        // A child process that runs for a while: what the backend is while upscaling.
        private static Process Sleeper() => Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1") { CreateNoWindow = true, RedirectStandardOutput = true }
            : new ProcessStartInfo("sleep", "30"))!;

        private static bool IsSuspended(Process p)
        {
            p.Refresh();
            if (OperatingSystem.IsWindows())
            {
                return p.Threads.Cast<ProcessThread>().All(t => t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended);
            }
            // /proc/<pid>/stat: the state follows the command name in parentheses; T is stopped.
            var stat = File.ReadAllText($"/proc/{p.Id}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..].StartsWith('T');
        }

        [Fact]
        public void APausedProcessIsFrozenUntilResumed()
        {
            using var p = Sleeper();
            try
            {
                Assert.True(ProcessControl.Suspend(p));
                Assert.True(IsSuspended(p));
                Assert.True(ProcessControl.Resume(p));
                Assert.False(IsSuspended(p));
                Assert.True(ProcessControl.Suspend(p), "a paused upscale can still be canceled");
            }
            finally
            {
                p.Kill(true);
                p.WaitForExit();
            }
            Assert.False(ProcessControl.Resume(p), "an ended process can't be resumed");
        }

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
            var (vm, _, _) = FolderRun(overwrite: true);
            Assert.False(vm.ContinueEnabled);
            vm.InterruptedRunSince = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            vm.InterruptedRunWorkflow = 0;
            Assert.True(vm.ContinueEnabled);
            vm.InterruptedRunWorkflow = 1;
            Assert.False(vm.ContinueEnabled, "another workflow's run");
            vm.InterruptedRunWorkflow = 0;
            vm.Upscaling = true;
            Assert.False(vm.ContinueEnabled, "not while upscaling");
        }

        [AvaloniaFact]
        public void PauseIsNotOfferedWhileAnEngineBuilds()
        {
            var (vm, _, _) = FolderRun(overwrite: true);
            Assert.False(vm.PauseEnabled, "nothing running");
            vm.Upscaling = true;
            Assert.True(vm.PauseEnabled);
            vm.EngineStatusText = "Building the TensorRT engine for x (a few minutes, once per model)...";
            Assert.False(vm.PauseEnabled);
        }

        // The interrupted run survives a restart: it's saved with the app state.
        [Fact]
        public void TheInterruptedRunIsSaved()
        {
            var vm = new MainWindowViewModel(new FakeBackendService([])) { InterruptedRunSince = 1762500000, InterruptedRunWorkflow = 2 };
            var json = JsonConvert.SerializeObject(vm, NewtonsoftJsonSuspensionDriver.Settings);
            var back = JsonConvert.DeserializeObject<MainWindowViewModel>(json, NewtonsoftJsonSuspensionDriver.Settings)!;
            Assert.Equal(1762500000, back.InterruptedRunSince);
            Assert.Equal(2, back.InterruptedRunWorkflow);
        }
    }
}
