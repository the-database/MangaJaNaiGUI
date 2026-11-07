using Avalonia.Collections;
using Splat;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MangaJaNaiConverterGui.Services
{
    public class BackendService : IBackendService
    {
        public const string NoModel = "No Model";

        private readonly IUpdateManagerService _updateManagerService;
        private readonly string? _inferenceDirectory;
        private readonly string? _stateDirectory;

        // The folder overrides are for tests (the env vars below are process-wide).
        public BackendService(IUpdateManagerService? updateManagerService = null, string? inferenceDirectory = null, string? stateDirectory = null)
        {
            _updateManagerService = updateManagerService ?? Locator.Current.GetService<IUpdateManagerService>()!;
            _inferenceDirectory = inferenceDirectory;
            _stateDirectory = stateDirectory;
        }

        private bool Installed => _updateManagerService?.IsInstalled ?? false;

        // MANGAJANAI_STATE_DIR redirects the saved app state (workflows, settings), so test and dev
        // launches never touch a real install's state (the Linux AppImage points it at the same folder
        // an install uses). An install keeps settings and user models in %APPDATA% (where 1.x kept them
        // too, so they carry over), on Linux in ~/.config; a dev build next to the working directory.
        public string AppStateFolder => _stateDirectory ??
            (Environment.GetEnvironmentVariable("MANGAJANAI_STATE_DIR") is { Length: > 0 } stateDir
                ? stateDir
                : Installed
                    ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), @"MangaJaNaiConverterGui")
                    : Path.GetFullPath(@"."));

        public string AppStateFilename => "appstate2.json";
        public string AppStatePath => Path.Join(AppStateFolder, AppStateFilename);
        public string LogsDirectory => Path.Combine(AppStateFolder, "logs");
        public string UserModelsDirectory => Path.Combine(AppStateFolder, "models");
        // TensorRT engines: rebuildable, but a few minutes each to build. An install that uses the
        // shared pack store keeps them in the store's folder (engines/mangajanai-gui), so they move
        // with it when the user puts the store on another drive (App Settings > Storage), and stay out
        // of the roaming profile. Otherwise beside the settings, as before.
        public string EngineCacheDirectory => UsesPackStore
            ? Path.Combine(StoreLocation.DefaultRoot(), "engines", "mangajanai-gui")
            : Path.Combine(AppStateFolder, "engines");

        private bool UsesPackStore => Installed && File.Exists(Path.Combine(InferenceDirectory, "aji-runtime.json"));

        // MANGAJANAI_BACKEND_DIR points a dev build at a local inference folder.
        public string InferenceDirectory => _inferenceDirectory ??
            (Environment.GetEnvironmentVariable("MANGAJANAI_BACKEND_DIR") is { Length: > 0 } backendDir
                ? backendDir
                : Path.Combine(AppContext.BaseDirectory, "inference"));

        // "mangajanai_upscale" -> "mangajanai_upscale.exe" on Windows; Linux binaries carry no suffix.
        public static string Exe(string stem) => OperatingSystem.IsWindows() ? stem + ".exe" : stem;

        // libaji itself: aji.dll, or libaji.so on Linux.
        private static string AjiLibrary => OperatingSystem.IsWindows() ? "aji.dll" : "libaji.so";

        public string BackendExePath => Path.Combine(InferenceDirectory, Exe("mangajanai_upscale"));

        public bool IsBackendInstalled => File.Exists(BackendExePath) && File.Exists(Path.Combine(InferenceDirectory, AjiLibrary));

        // libaji builds TensorRT-RTX for Windows only. aji_trtrtx.dll ships with libaji; its runtime is
        // the trt-rtx component: in the shared pack store (inference/aji-runtime.json "trtrtx"), or in
        // the inference folder (an install from before the store).
        public bool IsTensorRtRtxInstalled => UpscaleBackends.TensorRtRtxAvailable && File.Exists(Path.Combine(InferenceDirectory, "aji_trtrtx.dll")) &&
            (RuntimePack("trtrtx") is string rtx ? HasFile(rtx, "tensorrt_rtx_*.dll") : HasFile(InferenceDirectory, "tensorrt_rtx_*.dll"));

        // trtexec and cudart ship only in trt-runtime: TensorRT builds with the one, and both
        // TensorRT backends link the other. In the shared pack store, or in the inference folder.
        public bool IsTensorRtRuntimeInstalled => RuntimeFolderHasTrt(RuntimePack("trt") ?? InferenceDirectory);

        private static bool RuntimeFolderHasTrt(string dir) => File.Exists(Path.Combine(dir, Exe("trtexec"))) &&
            HasFile(dir, OperatingSystem.IsWindows() ? "cudart64_*.dll" : "libcudart.so*");

        // inference/aji-runtime.json, which the updater has the shared pack store write beside libaji:
        // the pack folders libaji loads TensorRT / TensorRT-RTX from, and the model pack folders.
        private JsonElement? RuntimeJson()
        {
            try
            {
                // AJI_RUNTIME_JSON names it where the inference folder is read-only (the AppImage), as
                // it does for libaji.
                string path = Environment.GetEnvironmentVariable("AJI_RUNTIME_JSON") is { Length: > 0 } env
                    ? env
                    : Path.Combine(InferenceDirectory, "aji-runtime.json");
                if (!File.Exists(path))
                {
                    return null;
                }
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.Clone();
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private string? RuntimePack(string key) =>
            RuntimeJson() is JsonElement j && j.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
            v.GetString() is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null;

        public IReadOnlyList<string> StoreModelsDirectories =>
            RuntimeJson() is JsonElement j && j.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Select(v => v.GetString() ?? "").Where(d => d.Length > 0).ToList()
                : [];

        public IReadOnlyList<string> ModelsDirectories => [UserModelsDirectory, .. StoreModelsDirectories, BundledModelsDirectory];

        private static bool HasFile(string dir, string pattern)
        {
            try
            {
                return Directory.Exists(dir) && Directory.EnumerateFiles(dir, pattern).Any();
            }
            catch (IOException)
            {
                return false;
            }
        }

        public string BundledModelsDirectory => Path.GetFullPath(Path.Combine(InferenceDirectory, "..", "models"));

        public bool AreModelsInstalled => ModelsDirectories.Any(d => ModelFiles(d).Any());

        private static string[] ModelFiles(string dir)
        {
            try
            {
                return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.onnx") : [];
            }
            catch (IOException)
            {
                return [];
            }
        }

        private AvaloniaList<string>? _allModels;

        public AvaloniaList<string> AllModels => _allModels ??= ScanModels();

        public bool RefreshModels()
        {
            var models = ScanModels();
            if (_allModels == null)
            {
                _allModels = models;
                return true;
            }
            // Unchanged: leave the list alone, so nothing bound to it is reset.
            if (_allModels.SequenceEqual(models))
            {
                return false;
            }
            // Update in place: every chain's model list shares this collection.
            _allModels.Clear();
            _allModels.AddRange(models);
            return true;
        }

        public string? ResolveModelPath(string? model)
        {
            if (string.IsNullOrWhiteSpace(model) || model == NoModel)
            {
                return null;
            }
            try
            {
                if (Path.IsPathRooted(model))
                {
                    return File.Exists(model) ? model : null;
                }
                foreach (var dir in ModelsDirectories)
                {
                    var path = Path.Combine(dir, model);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
            }
            catch (ArgumentException)
            {
            }
            return null;
        }

        private AvaloniaList<string> ScanModels()
        {
            var names = ModelsDirectories.SelectMany(ModelFiles)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            names.Add(NoModel);
            return new AvaloniaList<string>(names);
        }
    }
}
