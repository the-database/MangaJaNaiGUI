using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Headless.XUnit;
using MangaJaNaiConverterGui.Services;
using MangaJaNaiConverterGui.ViewModels;
using Xunit;

namespace MangaJaNaiConverterGui.Tests
{
    // An install whose engine cache moves into the store once its first-launch setup (the updater's
    // --auto) has put it on the store: an upgrade from a version before the store.
    public sealed class StoreSwitchBackendService(string state, string store) : IBackendService
    {
        public bool OnStore { get; set; }
        public string AppStateFolder => state;
        public string AppStateFilename => "appstate2.json";
        public string AppStatePath => Path.Combine(state, AppStateFilename);
        public string LogsDirectory => state;
        public string InferenceDirectory => state;
        public string BackendExePath => Path.Combine(state, "no-backend");
        public bool IsBackendInstalled => true;
        public bool IsTensorRtRtxInstalled => false;
        public bool IsTensorRtRuntimeInstalled => false;
        public string UserModelsDirectory => state;
        public IReadOnlyList<string> StoreModelsDirectories => [];
        public string BundledModelsDirectory => state;
        public IReadOnlyList<string> ModelsDirectories => [state];
        public bool AreModelsInstalled => true;
        public string EngineCacheDirectory => OnStore ? Path.Combine(store, "engines", "mangajanai-gui") : Path.Combine(state, "engines");
        public AvaloniaList<string> AllModels { get; } = [];
        public bool RefreshModels() => false;
        public string? ResolveModelPath(string? model) => null;
    }

    // The updater's --auto puts the install on the store.
    public sealed class AutoSetupUpdateManagerService(StoreSwitchBackendService backend) : IUpdateManagerService
    {
        public bool IsInstalled => true;
        public string AppVersion => "2.4.1";
        public string InstallDir => Path.GetTempPath();
        public string UpdaterPath => Path.Combine(InstallDir, "no-such-updater.exe");
        public Task<string?> CheckForUpdateAsync() => Task.FromResult<string?>(null);
        public void ApplyUpdateAndRestart() { }
        public Task<string?> DownloadUpdateAsync(Action<string>? onLine, CancellationToken ct) => Task.FromResult<string?>(null);
        public void ApplyUpdateOnExit() { }
        public string? TakeLastUpdateError() => null;
        public Task<string> GetComponentsJsonAsync() => Task.FromResult("");
        public Task<int> RunUpdaterStreamingAsync(string arguments, Action<string>? onLine, CancellationToken ct = default)
        {
            if (arguments == "--auto")
            {
                backend.OnStore = true;
            }
            return Task.FromResult(0);
        }
    }

    public class EngineMoveTests
    {
        // 2.4.0 moved the engines at startup only, before this setup ran, so an upgrade from before
        // the store moved them on its second launch (and could build them again in between).
        [AvaloniaFact]
        public async Task EnginesMoveRightAfterTheFirstLaunchSetup()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mangajanai-engines-" + Guid.NewGuid().ToString("N"));
            try
            {
                var state = Directory.CreateDirectory(Path.Combine(dir, "state")).FullName;
                var store = Path.Combine(dir, "store");
                var old = Directory.CreateDirectory(Path.Combine(state, "engines")).FullName;
                File.WriteAllText(Path.Combine(old, "aji-1234.engine"), "engine");
                var backend = new StoreSwitchBackendService(state, store);
                var vm = new MainWindowViewModel(backend, new AutoSetupUpdateManagerService(backend)) { LastRunVersion = "2.3.0" };

                vm.MoveEngineCacheFiles(); // startup: not on the store yet, nothing to move
                Assert.True(File.Exists(Path.Combine(old, "aji-1234.engine")));

                await vm.CheckAndExtractBackend(); // the update's first launch runs --auto
                Assert.True(File.Exists(Path.Combine(store, "engines", "mangajanai-gui", "aji-1234.engine")));
                Assert.False(Directory.Exists(old));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
