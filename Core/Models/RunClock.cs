using System.Globalization;

namespace ErRandoCompanion.Core.Models;

/// <summary>
/// Counts a run's played time: only while a character is in the world, so the title screen, loading screens and the
/// game being closed do not count, and never after the run's goal is met. Call <see cref="Tick"/> about once a second.
/// </summary>
public sealed class RunClock
{
    /// <summary>A longer gap between ticks (a sleep, a debugger, a hitch) counts as this much at most.</summary>
    public static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(5);

    /// <summary>How often the time is worth saving on its own, when nothing else has saved the run.</summary>
    public static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(1);

    private DateTime? _lastTick;
    private DateTime _lastSave = DateTime.MinValue;

    /// <summary>Adds the time since the last tick if the player was in the world. Returns true when it is time to save.</summary>
    public bool Tick(TrackerState state, bool inWorld, DateTime now)
    {
        var step = _lastTick is { } last ? now - last : TimeSpan.Zero;
        _lastTick = now;
        if (!inWorld || state.FinishedAt is not null || step <= TimeSpan.Zero)
            return false;

        state.PlayedSeconds += Math.Min(step.TotalSeconds, MaxStep.TotalSeconds);
        if (now - _lastSave < SaveEvery)
            return false;
        _lastSave = now;
        return true;
    }

    /// <summary>Forgets the last tick, for when another run is opened, so its time is not credited with the gap.</summary>
    public void Restart() => _lastTick = null;

    /// <summary>"1:02:03", or "2:03" under an hour.</summary>
    public static string Format(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
    }

    /// <summary>Checks per hour of play, once there is enough time for the number to mean something.</summary>
    public static double? PerHour(int checkedCount, double seconds) =>
        seconds >= 300 ? checkedCount / (seconds / 3600) : null;
}
