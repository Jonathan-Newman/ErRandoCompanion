using System.IO;

namespace ErRandoCompanion.Core.Persistence;

/// <summary>What changed about the open run since the last <see cref="RunOwnership.Check"/>.</summary>
public enum RunChange
{
    None,

    /// <summary>The owner saved: reload the run from disk to show its progress.</summary>
    Saved,

    /// <summary>The owner let go and this front end now owns the run: reload it from disk, then save freely.</summary>
    TookOver,
}

/// <summary>
/// Keeps the desktop app and the in-game overlay from overwriting each other. Whichever opens a run first owns
/// it; the other shows it read-only, follows the owner's saves, and takes over once the owner closes it.
/// </summary>
public sealed class RunOwnership(string directory, string owner) : IDisposable
{
    private RunLock? _lock;
    private string? _seedName;
    private DateTime? _seenSave;

    /// <summary>True while another front end owns the open run, so nothing may be saved here.</summary>
    public bool IsReadOnly => _seedName is not null && _lock is null;

    /// <summary>Who owns the open run when this front end does not.</summary>
    public string? HeldBy { get; private set; }

    /// <summary>What to tell the player while read-only; null otherwise.</summary>
    public string? Notice => IsReadOnly
        ? $"Read-only: this run is open in {HeldBy}. Nothing here is saved or auto-tracked until it is closed there; its progress shows here as it saves."
        : null;

    /// <summary>Opens <paramref name="seedName"/>: owns it if it is free, otherwise follows it read-only.</summary>
    public void Open(string seedName)
    {
        if (_seedName == seedName && _lock is not null)
            return;
        _lock?.Dispose();
        _lock = RunLock.TryAcquire(directory, seedName, owner);
        _seedName = seedName;
        HeldBy = _lock is null ? RunLock.Holder(directory, seedName) ?? "another program" : null;
        _seenSave = LastSave();
    }

    /// <summary>Call about once a second. Only a read-only front end ever gets anything other than None.</summary>
    public RunChange Check()
    {
        if (!IsReadOnly || _seedName is null)
            return RunChange.None;
        if (RunLock.TryAcquire(directory, _seedName, owner) is { } taken)
        {
            _lock = taken;
            HeldBy = null;
            _seenSave = LastSave();
            return RunChange.TookOver;
        }
        var saved = LastSave();
        if (saved == _seenSave)
            return RunChange.None;
        _seenSave = saved;
        return RunChange.Saved;
    }

    /// <summary>Whether another front end has <paramref name="seedName"/> open, so it must not be deleted here.</summary>
    public bool IsOpenElsewhere(string seedName) =>
        !(seedName == _seedName && _lock is not null) && RunLock.Holder(directory, seedName) is not null;

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }

    private DateTime? LastSave()
    {
        var path = Path.Combine(directory, Uri.EscapeDataString(_seedName!) + ".seed.json");
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }
}
