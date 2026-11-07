using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using MangaJaNaiConverterGui.Services;
using MangaJaNaiConverterGui.ViewModels;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    // An installed copy, for what only installed copies do.
    public sealed class InstalledUpdateManagerService : IUpdateManagerService
    {
        public bool IsInstalled => true;
        public string AppVersion => "2.3.1";
        public string InstallDir => Path.GetTempPath();
        public string UpdaterPath => Path.Combine(InstallDir, "no-such-updater.exe");
        public Task<string?> CheckForUpdateAsync() => Task.FromResult<string?>(null);
        public void ApplyUpdateAndRestart() { }
        public Task<string?> DownloadUpdateAsync(Action<string>? onLine, CancellationToken ct) => Task.FromResult<string?>(null);
        public void ApplyUpdateOnExit() { }
        public string? TakeLastUpdateError() => null;
        public Task<string> GetComponentsJsonAsync() => Task.FromResult("");
        public Task<int> RunUpdaterStreamingAsync(string arguments, Action<string>? onLine, CancellationToken ct = default) => Task.FromResult(0);
    }

    // The tests that set AJI_RUNTIME_STORE live in this one class, so they never run alongside each
    // other.
    public class StorageTests
    {
        private static string TempDir(string name)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"mangajanai-storage-{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void LocationJsonIsRead()
        {
            Assert.Equal(new StoreLocation.Info(@"C:\a", false, null, true),
                StoreLocation.Parse("""{"root": "C:\\a", "moved": false, "target": null, "available": true}"""));
            var moved = StoreLocation.Parse("""{"root": "C:\\a", "moved": true, "target": "F:\\b", "available": false}""")!;
            Assert.Equal(@"F:\b", moved.Folder);
            Assert.False(moved.Available);
            Assert.Null(StoreLocation.Parse("not json"));
        }

        [Fact]
        public void AMoveGoesIntoAFolderOfItsOwnWhenThePickedOneHasFiles()
        {
            var dir = TempDir("target");
            try
            {
                Assert.Equal(dir, StoreLocation.MoveTarget(dir));
                Assert.Equal(Path.Combine(dir, "new"), StoreLocation.MoveTarget(Path.Combine(dir, "new")));
                File.WriteAllText(Path.Combine(dir, "mine.txt"), "x");
                Assert.Equal(Path.Combine(dir, "JaNai shared runtime"), StoreLocation.MoveTarget(dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // Engines go with the store (so they move with it, and out of the roaming profile) once the
        // install uses it; an install from before the store keeps them beside the settings.
        [Fact]
        public void EnginesLiveInTheStoreOnceTheInstallUsesIt()
        {
            var dir = TempDir("engines");
            var old = Environment.GetEnvironmentVariable("AJI_RUNTIME_STORE");
            try
            {
                Environment.SetEnvironmentVariable("AJI_RUNTIME_STORE", Path.Combine(dir, "store"));
                var inference = Directory.CreateDirectory(Path.Combine(dir, "inference")).FullName;
                var state = Path.Combine(dir, "state");
                var backend = new BackendService(new InstalledUpdateManagerService(), inference, state);
                Assert.Equal(Path.Combine(state, "engines"), backend.EngineCacheDirectory);
                File.WriteAllText(Path.Combine(inference, "aji-runtime.json"), "{}");
                Assert.Equal(Path.Combine(dir, "store", "engines", "mangajanai-gui"), backend.EngineCacheDirectory);
                Assert.Equal(Path.Combine(state, "engines"), new BackendService(new FakeUpdateManagerService(), inference, state).EngineCacheDirectory); // a dev build
            }
            finally
            {
                Environment.SetEnvironmentVariable("AJI_RUNTIME_STORE", old);
                Directory.Delete(dir, true);
            }
        }

        [AvaloniaFact]
        public void TheButtonsFollowWhereTheStoreIs()
        {
            var vm = new MainWindowViewModel(new FakeBackendService([]), new InstalledUpdateManagerService());
            Assert.False(vm.ShowStorage, "nothing read yet");

            vm.Storage = new StoreLocation.Info(@"C:\aji", false, null, true);
            Assert.True(vm.ShowStorage);
            Assert.Equal(@"C:\aji", vm.StorageFolderText);
            Assert.False(vm.StorageMoved || vm.StorageMissing);

            vm.Storage = new StoreLocation.Info(@"C:\aji", true, @"F:\aji", true);
            Assert.Equal(@"F:\aji", vm.StorageFolderText);
            Assert.True(vm.StorageMoved);
            Assert.False(vm.StorageMissing);

            vm.Storage = new StoreLocation.Info(@"C:\aji", true, @"F:\aji", false);
            Assert.False(vm.StorageMoved);
            Assert.True(vm.StorageMissing);
            Assert.Contains("isn't available", vm.StorageText);

            Assert.False(new MainWindowViewModel(new FakeBackendService([]), new FakeUpdateManagerService()) { Storage = vm.Storage }.ShowStorage, "not in a dev build");
        }

        // Against a real aji-runtime (AJI_RUNTIME_EXE: a 0.2.0+ build), on a store in a temporary
        // folder: what the Storage buttons run.
        [Fact]
        public async Task MovingBackAndResettingThroughAjiRuntime()
        {
            var exe = Environment.GetEnvironmentVariable("AJI_RUNTIME_EXE");
            Assert.SkipUnless(File.Exists(exe), "set AJI_RUNTIME_EXE to an aji-runtime 0.2.0+ build");
            var dir = TempDir("move");
            var old = Environment.GetEnvironmentVariable("AJI_RUNTIME_STORE");
            try
            {
                var root = Path.Combine(dir, "store");
                Directory.CreateDirectory(Path.Combine(root, "packs", "trt", "1"));
                File.WriteAllText(Path.Combine(root, "store.json"), """{"format": 1}""");
                File.WriteAllBytes(Path.Combine(root, "packs", "trt", "1", "nvinfer.dll"), new byte[1_000_000]);
                Environment.SetEnvironmentVariable("AJI_RUNTIME_STORE", root);
                StoreLocation.AjiRuntimeOverride = exe;

                Assert.Equal(new StoreLocation.Info(root, false, null, true), await StoreLocation.GetAsync());

                var dest = Path.Combine(dir, "other-drive");
                var progress = new List<(long, long)>();
                Assert.Null(await StoreLocation.ChangeAsync(["move", dest], (d, t) => progress.Add((d, t))));
                Assert.True(progress[^1].Item1 == progress[^1].Item2 && progress[^1].Item2 > 1_000_000, "progress reaches the store's size");
                var moved = await StoreLocation.GetAsync();
                Assert.True(moved is { Moved: true, Available: true });
                Assert.Equal(Path.GetFullPath(dest), Path.GetFullPath(moved!.Folder));
                Assert.True(File.Exists(Path.Combine(root, "packs", "trt", "1", "nvinfer.dll")), "read through the default path");

                Assert.Contains("move it back instead", await StoreLocation.ChangeAsync(["reset"], null));

                Directory.Move(dest, dest + "-unplugged");
                Assert.True(await StoreLocation.GetAsync() is { Moved: true, Available: false });
                Assert.Contains("missing", await StoreLocation.ChangeAsync(["move", "--default"], null));
                Assert.Null(await StoreLocation.ChangeAsync(["reset"], null));
                Assert.Equal(new StoreLocation.Info(root, false, null, true), await StoreLocation.GetAsync());

                Directory.Move(dest + "-unplugged", dest);
                Assert.Null(await StoreLocation.ChangeAsync(["move", Path.Combine(dir, "d2")], null));
                Assert.Null(await StoreLocation.ChangeAsync(["move", "--default"], null));
                Assert.True(await StoreLocation.GetAsync() is { Moved: false });
            }
            finally
            {
                StoreLocation.AjiRuntimeOverride = null;
                Environment.SetEnvironmentVariable("AJI_RUNTIME_STORE", old);
                // a failed step can leave the default path a link (maybe a dangling one): remove the
                // link itself first, so cleaning up never hides that step's failure
                var link = new DirectoryInfo(Path.Combine(dir, "store"));
                try
                {
                    if (link.LinkTarget is not null)
                    {
                        link.Delete();
                    }
                    Directory.Delete(dir, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
