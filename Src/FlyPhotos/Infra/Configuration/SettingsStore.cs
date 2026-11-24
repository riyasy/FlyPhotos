using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace FlyPhotos.Infra.Configuration;

/// <summary>
/// Reads and writes the settings file so several app processes can share it: saves are serialized
/// by a named mutex and published atomically (temp file + replace), a save that started from stale
/// settings is merged per property with what another instance wrote meanwhile, and a file watcher
/// tells each instance when another one changed the file.
/// </summary>
internal sealed class SettingsStore : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const int MaxAttempts = 10;
    private const int RetryDelayMs = 25;
    private const string SettingsProperty = "Settings";

    private readonly string _path;
    private readonly int _debounceMs;
    private readonly Mutex _mutex;
    private readonly object _gate = new();

    // Held for a whole reload callback, and by Save around its write and its Reloaded raise, so
    // reloads never run concurrently and Reloaded is raised in the order the file changed.
    // Lock order is always _reloadGate, then the named mutex.
    private readonly object _reloadGate = new();

    // The file content each settings object was loaded from or last saved as: the baseline a later
    // save of that object is diffed against when another instance wrote the file in between.
    private readonly ConditionalWeakTable<AppSettings, string> _baselines = new();

    private FileSystemWatcher _watcher;
    private Timer _debounce;
    private string _lastKnown; // the content this store last knew the file to hold
    private bool _disposed;

    public SettingsStore(string path, string mutexName, int debounceMs = 200)
    {
        _path = path;
        _debounceMs = debounceMs;
        _mutex = new Mutex(false, mutexName);
    }

    /// <summary>
    /// Raised with settings this instance should adopt: file content another instance wrote, or the
    /// result of merging this instance's save with another instance's newer change.
    /// </summary>
    public event Action<AppSettings> Reloaded;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var json = ReadWithRetry();
            var settings = Deserialize(json);
            if (settings == null) return new AppSettings();
            lock (_gate) _lastKnown = json;
            Remember(settings, json);
            return settings;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not load settings; using defaults");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var ours = Serialize(settings);
        AppSettings adopted = null;

        lock (_reloadGate)
        {
            AcquireMutex();
            try
            {
                string baseline;
                lock (_gate) baseline = _baselines.TryGetValue(settings, out var b) ? b : _lastKnown;

                var toWrite = ours;
                var disk = File.Exists(_path) ? ReadWithRetry() : null;
                if (disk != null && baseline != null && disk != baseline)
                {
                    // Someone else wrote since this object's content was known: keep their changes
                    // except for the properties this instance changed.
                    var merged = Merge(disk, baseline, ours);
                    if (merged != null && merged != ours)
                    {
                        adopted = Deserialize(merged);
                        if (adopted != null) toWrite = merged;
                    }
                }

                WriteAtomically(toWrite);
                lock (_gate)
                {
                    _lastKnown = toWrite;
                    Remember(settings, ours);
                    if (adopted != null) Remember(adopted, toWrite);
                }
            }
            finally
            {
                _mutex.ReleaseMutex();
            }

            if (adopted != null) Reloaded?.Invoke(adopted);
        }
    }

    // A named Mutex is thread-affine, so acquire and release must happen on one thread.
    public Task SaveAsync(AppSettings settings) => Task.Run(() => Save(settings));

    public void StartWatching()
    {
        if (_watcher != null) return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(dir);

        _debounce = new Timer(_ => OnFileChanged(), null, Timeout.Infinite, Timeout.Infinite);
        var watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
        };
        watcher.Changed += (_, _) => ScheduleReload();
        watcher.Created += (_, _) => ScheduleReload();
        watcher.Renamed += (_, _) => ScheduleReload();
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _watcher?.Dispose();
        _debounce?.Dispose();
        _mutex.Dispose();
    }

    private void ScheduleReload()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                _debounce?.Change(_debounceMs, Timeout.Infinite);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnFileChanged()
    {
        lock (_reloadGate)
        {
            try
            {
                if (_disposed) return;

                AppSettings settings;
                // Under the mutex so the read and the _lastKnown update can't interleave with a save.
                AcquireMutex();
                try
                {
                    if (!File.Exists(_path)) return;
                    var json = ReadWithRetry();
                    lock (_gate)
                    {
                        if (_disposed || json == _lastKnown) return;
                    }

                    settings = Deserialize(json);
                    if (settings == null) return;
                    lock (_gate)
                    {
                        _lastKnown = json;
                        Remember(settings, json);
                    }
                }
                finally
                {
                    _mutex.ReleaseMutex();
                }

                Reloaded?.Invoke(settings);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Could not reload settings after external change");
            }
        }
    }

    private void AcquireMutex()
    {
        try
        {
            _mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died; we now own the mutex, which is what we wanted.
        }
    }

    // Caller holds _gate.
    private void Remember(AppSettings settings, string json) => _baselines.AddOrUpdate(settings, json);

    /// <summary>
    /// Starts from the properties of <paramref name="disk"/>'s "Settings" object and takes this
    /// instance's value for every property where <paramref name="ours"/> differs from
    /// <paramref name="baseline"/>. Returns the merged file content, or null if a document can't be parsed.
    /// </summary>
    private static string Merge(string disk, string baseline, string ours)
    {
        try
        {
            var root = JsonNode.Parse(disk);
            if (root?[SettingsProperty] is not JsonObject result ||
                JsonNode.Parse(baseline)?[SettingsProperty] is not JsonObject before ||
                JsonNode.Parse(ours)?[SettingsProperty] is not JsonObject mine)
                return null;

            foreach (var (name, value) in mine)
            {
                before.TryGetPropertyValue(name, out var old);
                if (!JsonNode.DeepEquals(value, old)) result[name] = value?.DeepClone();
            }

            // Round-trip through the typed model so the file keeps its usual shape and formatting.
            var merged = Deserialize(root.ToJsonString());
            return merged == null ? null : Serialize(merged);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(new SettingsWrapper { Settings = settings },
            JsonSourceGenerationContext.Default.SettingsWrapper);

    private static AppSettings Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.SettingsWrapper)?.Settings;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void WriteAtomically(string json)
    {
        var tmp = _path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        Retry(() =>
        {
            File.WriteAllText(tmp, json);
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        });
    }

    private string ReadWithRetry()
    {
        var result = "";
        Retry(() => result = File.ReadAllText(_path));
        return result;
    }

    // Windows briefly denies access (sharing violation or access denied) while another process
    // replaces the file, so both are treated as transient.
    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < MaxAttempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }
    }
}
