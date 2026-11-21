using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace FlyPhotos.Infra.Configuration;

/// <summary>
/// Reads and writes the settings file so several app processes can share it: saves are serialized
/// by a named mutex and published atomically (temp file + replace), and a file watcher tells each
/// instance when another one changed the file.
/// </summary>
internal sealed class SettingsStore : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const int MaxAttempts = 10;
    private const int RetryDelayMs = 25;

    private readonly string _path;
    private readonly int _debounceMs;
    private readonly Mutex _mutex;
    private readonly object _gate = new();

    private FileSystemWatcher _watcher;
    private Timer _debounce;
    private string _lastWritten;
    private bool _disposed;

    public SettingsStore(string path, string mutexName, int debounceMs = 200)
    {
        _path = path;
        _debounceMs = debounceMs;
        _mutex = new Mutex(false, mutexName);
    }

    /// <summary>Raised for file content this instance did not write itself.</summary>
    public event Action<AppSettings> Reloaded;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var json = ReadWithRetry();
            return Deserialize(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not load settings; using defaults");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(new SettingsWrapper { Settings = settings },
            JsonSourceGenerationContext.Default.SettingsWrapper);

        try
        {
            _mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died; we now own the mutex, which is what we wanted.
        }

        try
        {
            lock (_gate) _lastWritten = json;
            WriteAtomically(json);
        }
        finally
        {
            _mutex.ReleaseMutex();
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
        try
        {
            if (_disposed || !File.Exists(_path)) return;
            var json = ReadWithRetry();
            lock (_gate)
            {
                if (_disposed || json == _lastWritten) return;
            }

            var settings = Deserialize(json);
            if (settings == null) return;
            Reloaded?.Invoke(settings);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not reload settings after external change");
        }
    }

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

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }
    }
}
