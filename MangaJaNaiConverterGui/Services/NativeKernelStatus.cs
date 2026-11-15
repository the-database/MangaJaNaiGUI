using System.Collections.Generic;
using System.Linq;

namespace MangaJaNaiConverterGui.Services
{
    // Whether this run's models ran on libaji's native GPU kernels, from mangajanai_upscale's lines
    // (TensorRT and TensorRT-RTX; DirectML has no native kernels and prints none of them):
    //   NATIVE <model>: <what>       the model runs on native kernels (": <what>" may be missing)
    //   NATIVE_UNUSED <model>: <why> it builds a TensorRT or TensorRT-RTX engine instead
    //   NATIVE_OFF                   native kernels are off (AJI_NATIVE=off), so nothing per model
    // Each model is reported once a run. Lines may come from the backend's output thread while the
    // UI reads the text, hence the lock.
    public sealed class NativeKernelStatus
    {
        private readonly object _lock = new();
        // In the order the models loaded; Detail is what libaji runs (native) or why not.
        private readonly List<(string Model, string Detail, bool Native)> _models = [];
        private bool _off;
        private bool _offInSettings;

        // A new run; offInSettings: App Settings' "Use native GPU kernels" is off (the GUI sets
        // AJI_NATIVE=off for it).
        public void Reset(bool offInSettings)
        {
            lock (_lock)
            {
                _models.Clear();
                _off = false;
                _offInSettings = offInSettings;
            }
        }

        // Takes a line of the backend's output: true if it was one of the native-kernel lines.
        public bool Take(string line)
        {
            lock (_lock)
            {
                if (line == "NATIVE_OFF")
                {
                    _off = true;
                    return true;
                }
                bool native;
                string rest;
                if (line.StartsWith("NATIVE "))
                {
                    native = true;
                    rest = line["NATIVE ".Length..];
                }
                else if (line.StartsWith("NATIVE_UNUSED "))
                {
                    native = false;
                    rest = line["NATIVE_UNUSED ".Length..];
                }
                else
                {
                    return false;
                }
                var colon = rest.IndexOf(": ");
                var model = colon < 0 ? rest : rest[..colon];
                var detail = colon < 0 ? string.Empty : rest[(colon + 2)..];
                if (model.Length > 0 && !_models.Any(m => m.Model == model))
                {
                    _models.Add((model, detail, native));
                }
                return true;
            }
        }

        // The status bar's text: empty until the backend reports something.
        public string Text
        {
            get
            {
                lock (_lock)
                {
                    if (_off)
                    {
                        return _offInSettings ? "Native kernels off (App Settings)" : "Native kernels off (AJI_NATIVE)";
                    }
                    if (_models.Count == 0)
                    {
                        return string.Empty;
                    }
                    var used = _models.Count(m => m.Native);
                    if (used == _models.Count)
                    {
                        return "Native kernels";
                    }
                    var first = _models.First(m => !m.Native);
                    var why = Reason(first);
                    return used == 0
                        ? $"Native kernels not used — {why}"
                        : $"Native kernels for {used} of {_models.Count} models — {why}";
                }
            }
        }

        // The tooltip: every model's status, one per line.
        public string Tooltip
        {
            get
            {
                lock (_lock)
                {
                    if (_off)
                    {
                        return _offInSettings
                            ? "Native GPU kernels are turned off in App Settings, so every model runs on a TensorRT engine."
                            : "Native GPU kernels are turned off by the AJI_NATIVE environment variable, so every model runs on a TensorRT engine.";
                    }
                    return string.Join("\n", _models.Select(m => m.Native
                        ? (m.Detail.Length > 0 ? $"{m.Model}: native kernels ({m.Detail})" : $"{m.Model}: native kernels")
                        : $"{m.Model}: not on native kernels ({(m.Detail.Length > 0 ? m.Detail : "no reason given")})"));
                }
            }
        }

        // Every model this run reported so far runs on native kernels (false with them off, or
        // before any model loaded).
        public bool AllNative
        {
            get
            {
                lock (_lock)
                {
                    return !_off && _models.Count > 0 && _models.All(m => m.Native);
                }
            }
        }

        private static string Reason((string Model, string Detail, bool Native) m) =>
            m.Detail.Length > 0 ? $"{m.Model}: {m.Detail}" : m.Model;
    }
}
