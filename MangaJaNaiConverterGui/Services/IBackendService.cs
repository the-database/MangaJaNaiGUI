using Avalonia.Collections;
using System.Collections.Generic;

namespace MangaJaNaiConverterGui.Services
{
    // Where the upscaling backend (mangajanai_upscale + libaji) and the app's data live.
    public interface IBackendService
    {
        string AppStateFolder { get; }
        string AppStateFilename { get; }
        string AppStatePath { get; }
        string LogsDirectory { get; }

        // The inference folder: libaji, its runtimes and mangajanai_upscale.
        string InferenceDirectory { get; }
        string BackendExePath { get; }
        bool IsBackendInstalled { get; }

        // The TensorRT-RTX backend (the trt-rtx component) and the TensorRT runtime (trt-runtime),
        // which TensorRT-RTX needs as well (its CUDA runtime, and TensorRT for the fallback).
        bool IsTensorRtRtxInstalled { get; }
        bool IsTensorRtRuntimeInstalled { get; }

        // Models the user added (shown first) and the default models: the shared pack store's model
        // packs (inference/aji-runtime.json "models"), and the models folder an install from before the
        // store has. ModelsDirectories is all of them, in lookup order.
        string UserModelsDirectory { get; }
        IReadOnlyList<string> StoreModelsDirectories { get; }
        string BundledModelsDirectory { get; }
        IReadOnlyList<string> ModelsDirectories { get; }
        bool AreModelsInstalled { get; }

        // TensorRT (*.engine) and TensorRT-RTX (*.rtxengine + *.jitcache) engines built for this GPU,
        // one per model and tile size. Each backend's stale cleanup only touches its own files.
        string EngineCacheDirectory { get; }

        // The ONNX models in all the folders, plus "No Model". Cached; RefreshModels rescans, and says
        // whether the list changed.
        AvaloniaList<string> AllModels { get; }
        bool RefreshModels();

        // A chain's model file as the backend finds it: the user's models folder first, then the
        // default ones (a full path as it is). Null for No Model or a file that isn't there.
        string? ResolveModelPath(string? model);
    }
}
