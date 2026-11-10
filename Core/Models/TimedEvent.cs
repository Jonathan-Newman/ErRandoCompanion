namespace ErRandoCompanion.Core.Models;

/// <summary>Something that happened in a run, and the run's played time when it did.</summary>
public sealed class TimedEvent
{
    public string Name { get; set; } = "";
    public double Seconds { get; set; }
}
