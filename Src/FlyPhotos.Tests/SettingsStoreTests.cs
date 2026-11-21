using System.Collections.Concurrent;
using System.Text.Json;
using FlyPhotos.Infra.Configuration;

namespace FlyPhotos.Tests;

/// <summary>
/// SettingsStore against a real temp folder. Waits are bounded polls, so a pass is fast and a
/// failure costs at most the timeout.
/// </summary>
[TestClass]
public sealed class SettingsStoreTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private string _dir = "";
    private string _path = "";
    private string _mutex = "";
    private readonly List<SettingsStore> _stores = [];

    [TestInitialize]
    public void Init()
    {
        _dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "FlySettingsStore_" + Guid.NewGuid().ToString("N"))).FullName;
        _path = Path.Combine(_dir, "usersettings.json");
        _mutex = "FlySettingsTest_" + Guid.NewGuid().ToString("N");
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var s in _stores) s.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private SettingsStore NewStore(int debounceMs = 50)
    {
        var s = new SettingsStore(_path, _mutex, debounceMs);
        _stores.Add(s);
        return s;
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    [TestMethod]
    public void Load_returns_defaults_when_file_missing()
    {
        var settings = NewStore().Load();
        Assert.IsNotNull(settings);
        Assert.IsFalse(settings.AllowMultiInstance);
    }

    [TestMethod]
    public void Load_returns_defaults_when_file_corrupt()
    {
        File.WriteAllText(_path, "{ not json");
        var settings = NewStore().Load();
        Assert.IsNotNull(settings);
        Assert.IsFalse(settings.AllowMultiInstance);
    }

    [TestMethod]
    public void Save_then_Load_round_trips_on_first_launch()
    {
        var store = NewStore();
        store.Save(new AppSettings { ThumbnailSize = 123 });

        Assert.AreEqual(123, NewStore().Load().ThumbnailSize);
        Assert.AreEqual(0, Directory.GetFiles(_dir, "*.tmp").Length);
    }

    [TestMethod]
    public void Save_from_one_store_raises_Reloaded_in_the_other_with_new_values()
    {
        var a = NewStore();
        var b = NewStore();
        // Create the file first so the save under test goes through File.Replace, not the first-launch Move.
        a.Save(new AppSettings { ThumbnailSize = 11 });

        var received = new ConcurrentQueue<AppSettings>();
        b.Reloaded += s => received.Enqueue(s);
        b.StartWatching();

        a.Save(new AppSettings { ThumbnailSize = 77 });

        Assert.IsTrue(WaitFor(() => received.Any(s => s.ThumbnailSize == 77), Timeout));
    }

    [TestMethod]
    public void Content_equal_to_own_earlier_write_is_still_reloaded_after_another_change()
    {
        var a = NewStore();
        var b = NewStore();
        var received = new ConcurrentQueue<AppSettings>();
        a.Reloaded += s => received.Enqueue(s);
        a.StartWatching();

        a.Save(new AppSettings { ThumbnailSize = 10 });   // X
        b.Save(new AppSettings { ThumbnailSize = 20 });   // Y
        Assert.IsTrue(WaitFor(() => received.Count == 1, Timeout), "A should reload Y");
        b.Save(new AppSettings { ThumbnailSize = 10 });   // X again, byte-identical to A's own write

        Assert.IsTrue(WaitFor(() => received.Count == 2, Timeout), $"A reloaded {received.Count} time(s)");
        Assert.AreEqual(20, received.ElementAt(0).ThumbnailSize);
        Assert.AreEqual(10, received.ElementAt(1).ThumbnailSize);
    }

    [TestMethod]
    public void Stale_save_merges_with_another_instances_newer_change()
    {
        NewStore().Save(new AppSettings());
        var a = NewStore();
        var b = NewStore();
        var sa = a.Load();
        var sb = b.Load();
        var received = new ConcurrentQueue<AppSettings>();
        a.Reloaded += s => received.Enqueue(s);   // A is not watching: it never saw B's save

        sb.ThumbnailSize = 77;
        b.Save(sb);
        sa.AllowMultiInstance = true;
        a.Save(sa);

        var onDisk = NewStore().Load();
        Assert.AreEqual(77, onDisk.ThumbnailSize);
        Assert.IsTrue(onDisk.AllowMultiInstance);
        Assert.IsTrue(WaitFor(() => received.Any(s => s.ThumbnailSize == 77 && s.AllowMultiInstance), Timeout),
            "A should adopt the merged settings");
    }

    [TestMethod]
    public void Stale_save_of_the_same_property_lets_the_later_saver_win()
    {
        NewStore().Save(new AppSettings());
        var a = NewStore();
        var b = NewStore();
        var sa = a.Load();
        var sb = b.Load();

        sb.ThumbnailSize = 77;
        b.Save(sb);
        sa.ThumbnailSize = 88;
        a.Save(sa);

        Assert.AreEqual(88, NewStore().Load().ThumbnailSize);
    }

    [TestMethod]
    public void Own_save_does_not_raise_Reloaded()
    {
        var a = NewStore();
        var fired = 0;
        a.Reloaded += _ => Interlocked.Increment(ref fired);
        a.StartWatching();

        a.Save(new AppSettings { ThumbnailSize = 55 });
        Thread.Sleep(1000);

        Assert.AreEqual(0, Volatile.Read(ref fired));
    }

    [TestMethod]
    public void Concurrent_saves_from_two_stores_leave_valid_json()
    {
        var a = NewStore();
        var b = NewStore();
        a.Save(new AppSettings { ThumbnailSize = 1 });

        var errors = new ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var text = File.ReadAllText(_path);
                    // Never a torn file: every successful read is complete, valid JSON.
                    JsonSerializer.Deserialize(text, JsonSourceGenerationContext.Default.SettingsWrapper);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Windows briefly denies readers while File.Replace swaps the file; real readers retry.
                }
                catch (Exception e) { errors.Enqueue(e); }
            }
        });

        var ta = Task.Run(() => { for (var i = 0; i < 50; i++) a.Save(new AppSettings { ThumbnailSize = 1000 + i }); });
        var tb = Task.Run(() => { for (var i = 0; i < 50; i++) b.Save(new AppSettings { ThumbnailSize = 2000 + i }); });
        Task.WaitAll(ta, tb);
        stop.Cancel();
        reader.Wait();

        Assert.IsTrue(errors.IsEmpty, string.Join(" | ", errors.Take(3).Select(e => e.GetType().Name + e.Message)));
        var wrapper = JsonSerializer.Deserialize(File.ReadAllText(_path),
            JsonSourceGenerationContext.Default.SettingsWrapper);
        Assert.IsNotNull(wrapper?.Settings);
        var size = wrapper.Settings.ThumbnailSize;
        Assert.IsTrue(size is >= 1000 and < 1050 || size is >= 2000 and < 2050, $"unexpected {size}");
    }
}

