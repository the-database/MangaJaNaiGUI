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

        // A portable copy keeps its store inside its folder (aji-runtime.conf at the install root names
        // it), and its engines go there too.
        [Fact]
        public void EnginesFollowTheStoreTheSettingsFileNames()
        {
            var dir = TempDir("engines-conf");
            var old = Environment.GetEnvironmentVariable("AJI_RUNTIME_STORE");
            try
            {
                Environment.SetEnvironmentVariable("AJI_RUNTIME_STORE", null);
                var inference = Directory.CreateDirectory(Path.Combine(dir, "app", "inference")).FullName;
                File.WriteAllText(Path.Combine(inference, "aji-runtime.json"), "{}");
                var backend = new BackendService(new InstalledUpdateManagerService(), inference, Path.Combine(dir, "state"));
                Assert.Equal(Path.Combine(StoreLocation.DefaultRoot(), "engines", "mangajanai-gui"), backend.EngineCacheDirectory);

                var conf = Path.Combine(dir, "app", "aji-runtime.conf");
                File.WriteAllText(conf, "# where\nstore = runtime\n");
                Assert.Equal(Path.Combine(dir, "app", "runtime", "engines", "mangajanai-gui"), backend.EngineCacheDirectory);
                File.WriteAllText(conf, $"store = \"{Path.Combine(dir, "custom")}\"\n");
                Assert.Equal(Path.Combine(dir, "custom", "engines", "mangajanai-gui"), backend.EngineCacheDirectory);
                // `use --shared` writes an empty value
                File.WriteAllText(conf, "; shared\nstore =\n");
                Assert.Equal(Path.Combine(StoreLocation.DefaultRoot(), "engines", "mangajanai-gui"), backend.EngineCacheDirectory);
                Assert.Null(StoreLocation.ReadStoreSetting(conf));
                File.WriteAllText(conf, "stor = runtime\n");
                Assert.Null(StoreLocation.ReadStoreSetting(conf));
            }
            finally
            {
                Environment.SetEnvironmentVariable("AJI_RUNTIME_STORE", old);
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void TheUpdatersChoiceIsRead()
        {
            var inside = StoreLocation.ParseChoice("""
                {"choice": "inside", "root": "D:\\p\\runtime", "moved": false, "target": null, "available": true,
                 "inside": "D:\\p\\runtime", "shared": "C:\\u\\aji", "installed": true, "bytes": 1234}
                """);
            Assert.NotNull(inside);
            Assert.Equal(StoreLocation.StoreChoice.Inside, inside.Choice);
            Assert.Equal(@"D:\p\runtime", inside.Folder);
            Assert.Equal(@"C:\u\aji", inside.SharedFolder);
            Assert.True(inside.Installed);
            Assert.Equal(1234, inside.Bytes);
            Assert.False(inside.IsShared || inside.MovedHere || inside.Missing);
            Assert.Equal("", inside.Text);

            // a shared store moved elsewhere: its files are in the target
            var shared = StoreLocation.ParseChoice("""
                {"choice": "shared", "root": "C:\\u\\aji", "moved": true, "target": "F:\\aji", "available": true,
                 "inside": "D:\\p\\runtime", "shared": "C:\\u\\aji", "installed": false, "bytes": 0}
                """)!;
            Assert.True(shared.IsShared && shared.MovedHere);
            Assert.Equal(@"F:\aji", shared.Folder);
            Assert.Equal(@"F:\aji", shared.SharedFolder);

            var custom = StoreLocation.ParseChoice("""
                {"choice": "custom", "root": "E:\\mine", "moved": false, "target": null, "available": false,
                 "inside": "D:\\p\\runtime", "shared": "C:\\u\\aji", "installed": true}
                """)!;
            Assert.True(custom.Missing);
            Assert.Null(custom.Bytes);
            Assert.Contains("isn't available", custom.Text);

            Assert.Null(StoreLocation.ParseChoice("""{"choice": "elsewhere", "root": "C:\\a"}"""));
            Assert.Null(StoreLocation.ParseChoice("""{"choice": "inside"}"""));
            Assert.Null(StoreLocation.ParseChoice("Usage: MangaJaNaiUpdater ..."));
        }

        // "A folder I choose": a store or a new/empty folder is used as it is, anything else gets a
        // folder of its own inside; the inside and shared folders are those choices.
        [Fact]
        public void AChosenFolderIsUsedAsAStore()
        {
            var dir = TempDir("choose");
            try
            {
                Assert.Equal(dir, StoreLocation.ChoiceTarget(dir));
                File.WriteAllText(Path.Combine(dir, "mine.txt"), "x");
                Assert.Equal(Path.Combine(dir, "JaNai shared runtime"), StoreLocation.ChoiceTarget(dir));
                File.WriteAllText(Path.Combine(dir, "store.json"), "{}");
                Assert.True(StoreLocation.IsStore(dir));
                Assert.Equal(dir, StoreLocation.ChoiceTarget(dir));

                var info = new StoreLocation.Info(Path.Combine(dir, "app", "runtime"), false, null, true)
                {
                    Choice = StoreLocation.StoreChoice.Inside,
                    Inside = Path.Combine(dir, "app", "runtime"),
                    Shared = Path.Combine(dir, "aji"),
                };
                Assert.Equal("inside", StoreLocation.ChoiceArgument(info, Path.Combine(dir, "app", "runtime") + Path.DirectorySeparatorChar));
                Assert.Equal("shared", StoreLocation.ChoiceArgument(info, Path.Combine(dir, "aji")));
                Assert.Equal(Path.Combine(dir, "other"), StoreLocation.ChoiceArgument(info, Path.Combine(dir, "other")));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [AvaloniaFact]
        public void TheOptionsFollowTheChoice()
        {
            var vm = new MainWindowViewModel(new FakeBackendService([]), new InstalledUpdateManagerService());
            vm.Storage = new StoreLocation.Info(@"D:\p\runtime", false, null, true)
            {
                Choice = StoreLocation.StoreChoice.Inside, Inside = @"D:\p\runtime", Shared = @"C:\u\aji", Installed = true,
            };
            Assert.True(vm.ShowStorage && vm.HasStorageChoice && vm.StorageIsInside);
            Assert.False(vm.StorageIsShared || vm.StorageIsCustom);
            Assert.False(vm.AllowSharedStorageChanges, "Move... moves the shared folder, which this copy doesn't use");
            Assert.True(vm.AllowStorageChanges);
            Assert.Equal(@"D:\p\runtime", vm.StorageInsideFolder);
            Assert.Equal(@"C:\u\aji", vm.StorageSharedFolder);
            Assert.Equal("No folder chosen", vm.StorageCustomFolder);

            vm.Storage = new StoreLocation.Info(@"E:\mine", false, null, false)
            {
                Choice = StoreLocation.StoreChoice.Custom, Inside = @"D:\p\runtime", Shared = @"C:\u\aji", Installed = false,
            };
            Assert.True(vm.StorageIsCustom && vm.StorageCustomMissing && vm.StorageUnavailable);
            Assert.False(vm.StorageMissing, "nothing of this copy's is in the missing folder");
            Assert.Equal(@"E:\mine", vm.StorageCustomFolder);
            vm.Storage = vm.Storage with { Installed = true };
            Assert.True(vm.StorageMissing, "this copy's components are in the missing folder");

            vm.Storage = new StoreLocation.Info(@"C:\u\aji", false, null, true)
            {
                Choice = StoreLocation.StoreChoice.Shared, Inside = @"D:\p\runtime", Shared = @"C:\u\aji", Installed = true,
            };
            Assert.True(vm.StorageIsShared && vm.AllowSharedStorageChanges);
        }

        // The updater as a script: `--storage --json` prints the choice, `--storage <x>` progress and a
        // result.
        private static string FakeUpdater(string dir, string json, int switchExit)
        {
            var path = Path.Combine(dir, "updater.cmd");
            File.WriteAllText(path, string.Join("\r\n",
                "@echo off",
                "if \"%2\"==\"--json\" (",
                $"  echo {json}",
                "  exit /b 0",
                ")",
                "echo AJI_MOVE 0 2000000000",
                "echo AJI_MOVE 2000000000 2000000000",
                $"if {switchExit}==0 (echo The components are now kept in %2.& exit /b 0)",
                "echo That folder is on a network drive. 1>&2",
                $"exit /b {switchExit}",
                ""));
            return path;
        }

        [Fact(Skip = WindowsOnly.Reason, SkipType = typeof(WindowsOnly), SkipUnless = nameof(WindowsOnly.OnWindows))]
        public async Task SwitchingRunsTheUpdater()
        {
            var dir = TempDir("switch");
            try
            {
                StoreLocation.UpdaterOverride = FakeUpdater(dir,
                    """{"choice": "inside", "root": "D:\\p\\runtime", "available": true, "inside": "D:\\p\\runtime", "shared": "C:\\u\\aji", "installed": true, "bytes": 5}""", 0);
                var choice = await StoreLocation.GetChoiceAsync();
                Assert.True(choice is { Choice: StoreLocation.StoreChoice.Inside, Installed: true, Bytes: 5 });
                var progress = new List<(long, long)>();
                Assert.Null(await StoreLocation.SwitchAsync("shared", (d, t) => progress.Add((d, t))));
                Assert.Equal((2_000_000_000L, 2_000_000_000L), progress[^1]);

                StoreLocation.UpdaterOverride = FakeUpdater(dir, "Usage", 1);
                Assert.Null(await StoreLocation.GetChoiceAsync());
                Assert.Equal("That folder is on a network drive.", await StoreLocation.SwitchAsync(@"\\server\share", null));
            }
            finally
            {
                StoreLocation.UpdaterOverride = null;
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
