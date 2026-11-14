#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FlyPhotos.Services;

/// <summary>A file renamed from <see cref="OldPath"/> to <see cref="NewPath"/>, both supported images.</summary>
internal readonly record struct FileRename(string OldPath, string NewPath);

/// <summary>
/// One batch of changes seen in a watched folder. Added files carry their sort metadata, read while
/// probing them, so the consumer never touches the disk. Renames between two supported names are reported
/// as pairs, so the consumer can keep the same photo (and the viewer on it). <see cref="Rescan"/> means
/// events were lost (watcher buffer overflow or a watcher error) and the folder must be listed again; the
/// lists are empty then.
/// </summary>
internal sealed record FolderChanges(IReadOnlyList<FileStamp> Added, IReadOnlyList<string> Removed,
    IReadOnlyList<FileRename> Renamed, bool Rescan);

/// <summary>
/// Watches one folder (not its subfolders) for supported images being added, removed or renamed, and
/// reports them in batches on a ThreadPool thread.
/// <para>
/// A new file is only reported once it has been quiet for <c>quietMs</c> and can be opened while denying
/// writers — i.e. whoever created it has finished writing. A generator writing images in chunks therefore
/// produces one "added" per image instead of a half-written file.
/// </para>
/// <para>
/// Paths in a batch are reported as seen: a removed path may not be in the consumer's list (Fly's own
/// delete), and an added path may already be in it (a file listed and then reported). The consumer
/// ignores both. Removals are applied before additions, so a file replaced in place comes back fresh.
/// </para>
/// </summary>
internal sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Func<string, bool> _isSupported;
    private readonly int _quietMs;
    private readonly Timer _timer;
    private readonly Lock _lock = new();

    // Guarded by _lock. Path -> tick of its latest event; reported once quiet and readable.
    private readonly Dictionary<string, long> _pendingAdds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingRemoves = new(StringComparer.OrdinalIgnoreCase);
    // Current name -> the name the file had when the batch started, so a chain a->b->c reports a->c.
    private readonly Dictionary<string, string> _pendingRenames = new(StringComparer.OrdinalIgnoreCase);
    private bool _rescan;
    private bool _timerArmed;
    private bool _disposed;

    /// <summary>Raised on a ThreadPool thread; the sender is this watcher.</summary>
    public event EventHandler<FolderChanges>? Changed;

    public string Folder { get; }

    /// <param name="isSupported">Whether a path has a supported image extension.</param>
    /// <param name="quietMs">How long a new file must go without further events before it is reported.</param>
    public FolderWatcher(string folder, Func<string, bool> isSupported, int quietMs = 300)
    {
        Folder = folder;
        _isSupported = isSupported;
        _quietMs = quietMs;
        _timer = new Timer(_ => Flush());
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024
        };
        _watcher.Created += OnCreatedOrChanged;
        _watcher.Changed += OnCreatedOrChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        if (!_isSupported(e.FullPath)) return;
        lock (_lock)
        {
            // A Changed event for a file that is not pending is an edit of a listed file — not tracked.
            if (e.ChangeType == WatcherChangeTypes.Changed && !_pendingAdds.ContainsKey(e.FullPath)) return;
            _pendingAdds[e.FullPath] = Environment.TickCount64;
            ArmTimer();
        }
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (!_isSupported(e.FullPath)) return;
        lock (_lock)
        {
            RemoveLocked(e.FullPath);
            ArmTimer();
        }
    }

    // Caller holds _lock. The file at `path` is gone: a not-yet-reported new file is simply dropped, and a
    // renamed one is reported as the removal of the name it started the batch with.
    private void RemoveLocked(string path)
    {
        if (_pendingAdds.Remove(path)) return;
        _pendingRemoves.Add(_pendingRenames.Remove(path, out var original) ? original : path);
    }

    // Supported -> supported with the same extension is a rename (the same photo, new name). Otherwise either
    // side may be unsupported: "x.tmp" -> "x.png" is how many tools finish writing an image (an addition), and
    // "x.png" -> "x.bak" takes an image away (a removal). A changed image extension ("x.png" -> "x.jpg") is a
    // removal plus an addition, since the photo's format-derived flags would no longer hold.
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        bool oldSupported = _isSupported(e.OldFullPath);
        bool newSupported = _isSupported(e.FullPath);
        if (!oldSupported && !newSupported) return;
        bool sameExtension = string.Equals(Path.GetExtension(e.OldFullPath), Path.GetExtension(e.FullPath),
            StringComparison.OrdinalIgnoreCase);
        lock (_lock)
        {
            if (!newSupported)
                RemoveLocked(e.OldFullPath);
            else if (!oldSupported || _pendingAdds.Remove(e.OldFullPath))
                _pendingAdds[e.FullPath] = Environment.TickCount64; // still a new file, under its new name
            else if (!sameExtension)
            {
                RemoveLocked(e.OldFullPath);
                _pendingAdds[e.FullPath] = Environment.TickCount64;
            }
            else
                _pendingRenames[e.FullPath] = _pendingRenames.Remove(e.OldFullPath, out var original) ? original : e.OldFullPath;
            ArmTimer();
        }
    }

    // Internal so tests can raise the event-loss path directly; a real buffer overflow can't be forced reliably.
    internal void OnError(object? sender, ErrorEventArgs e)
    {
        lock (_lock)
        {
            _rescan = true;
            _pendingAdds.Clear();
            _pendingRemoves.Clear();
            _pendingRenames.Clear();
            ArmTimer();
        }
    }

    // Caller holds _lock. A fixed interval rather than a restart on every event, so a steady stream of
    // events can never starve the batch.
    private void ArmTimer()
    {
        if (_timerArmed || _disposed) return;
        _timerArmed = true;
        _timer.Change(_quietMs, Timeout.Infinite);
    }

    private void Flush()
    {
        List<string>? candidates = null;
        bool rescan;
        lock (_lock)
        {
            if (_disposed) return;
            rescan = _rescan;
            _rescan = false;
            // A probing flush stays armed until it ends, so events meanwhile can't start a second, overlapping
            // one: it would probe the same files again (piling up pool threads on a hung network share) and race
            // this one's batch to the consumer.
            if (rescan) _timerArmed = false;
            long now = Environment.TickCount64;
            if (!rescan)
                foreach (var (path, tick) in _pendingAdds)
                    if (now - tick >= _quietMs)
                        (candidates ??= []).Add(path);
        }
        if (rescan)
        {
            Changed?.Invoke(this, new FolderChanges([], [], [], Rescan: true));
            return;
        }

        // Probing opens files, so it runs outside the lock; events keep arriving meanwhile. A file that is
        // ready or gone is settled; one still being written stays pending for the next tick.
        List<FileStamp>? ready = null;
        List<string>? settled = null;
        if (candidates != null)
            foreach (var path in candidates)
            {
                var result = Probe(path, out var stamp);
                if (result == ProbeResult.Busy) continue;
                (settled ??= []).Add(path);
                if (result == ProbeResult.Ready) (ready ??= []).Add(stamp);
            }

        FolderChanges? changes = null;
        lock (_lock)
        {
            if (_disposed) return;
            _timerArmed = false;
            if (settled != null)
                foreach (var p in settled) _pendingAdds.Remove(p);

            // A removal whose path is being re-created (a file replaced in place: delete, then write) waits for
            // the new file, so both arrive in one batch and the consumer can keep the viewer on it.
            var removed = new List<string>(_pendingRemoves.Count);
            foreach (var p in _pendingRemoves)
                if (!_pendingAdds.ContainsKey(p)) removed.Add(p);
            foreach (var p in removed) _pendingRemoves.Remove(p);
            var renamed = new List<FileRename>(_pendingRenames.Count);
            foreach (var (current, original) in _pendingRenames)
                // a->b->a is no change; a case-only rename (a.png -> A.png) is one, so compare exactly.
                if (!current.Equals(original, StringComparison.Ordinal))
                    renamed.Add(new FileRename(original, current));
            _pendingRenames.Clear();
            if (ready != null || removed.Count > 0 || renamed.Count > 0)
                changes = new FolderChanges(ready ?? [], removed, renamed, Rescan: false);

            if (_pendingAdds.Count > 0 || _pendingRemoves.Count > 0) ArmTimer();
        }

        if (changes != null) Changed?.Invoke(this, changes);
    }

    private enum ProbeResult { Ready, Busy, Gone }

    // Opening while denying writers fails as long as anyone still holds the file open for writing. The same
    // handle gives the sort metadata, so a new file costs one open and no separate stat.
    private static ProbeResult Probe(string path, out FileStamp stamp)
    {
        stamp = default;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            stamp = new FileStamp(path, File.GetLastWriteTimeUtc(handle).Ticks,
                File.GetCreationTimeUtc(handle).Ticks, RandomAccess.GetLength(handle));
            return ProbeResult.Ready;
        }
        catch (FileNotFoundException) { return ProbeResult.Gone; }
        catch (DirectoryNotFoundException) { return ProbeResult.Gone; }
        catch (UnauthorizedAccessException) { return ProbeResult.Gone; }
        catch (IOException) { return ProbeResult.Busy; }
    }

    // Disabling and disposing the watcher stops its events; _disposed stops a timer tick already queued.
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }
}
