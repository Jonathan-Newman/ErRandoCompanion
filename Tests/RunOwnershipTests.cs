using System.IO;
using ErRandoCompanion.Core.Persistence;
using Xunit;

namespace ErRandoCompanion.Tests;

public sealed class RunOwnershipTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ErRandoCompanionOwnership", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string SavePath(string run) => Path.Combine(_directory, Uri.EscapeDataString(run) + ".seed.json");

    [Fact]
    public void TheSecondToOpenARunFollowsItReadOnlyThenTakesOver()
    {
        var app = new RunOwnership(_directory, "the desktop app");
        using var overlay = new RunOwnership(_directory, "the in-game overlay");

        app.Open("Seed 1");
        overlay.Open("Seed 1");
        Assert.False(app.IsReadOnly);
        Assert.True(overlay.IsReadOnly);
        Assert.Equal("the desktop app", overlay.HeldBy);
        Assert.Contains("the desktop app", overlay.Notice);
        Assert.Equal(RunChange.None, overlay.Check());

        // The owner saves: the follower is told to reload.
        File.WriteAllText(SavePath("Seed 1"), "{}");
        File.SetLastWriteTimeUtc(SavePath("Seed 1"), DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(RunChange.Saved, overlay.Check());
        Assert.Equal(RunChange.None, overlay.Check());

        // The owner closes: the follower takes over and may save.
        app.Dispose();
        Assert.Equal(RunChange.TookOver, overlay.Check());
        Assert.False(overlay.IsReadOnly);
        Assert.Null(overlay.Notice);
    }

    [Fact]
    public void DifferentRunsDoNotBlockEachOtherAndOpenRunsCannotBeDeleted()
    {
        using var app = new RunOwnership(_directory, "the desktop app");
        using var overlay = new RunOwnership(_directory, "the in-game overlay");

        app.Open("Seed 1");
        overlay.Open("Seed 2");
        Assert.False(app.IsReadOnly);
        Assert.False(overlay.IsReadOnly);

        Assert.True(overlay.IsOpenElsewhere("Seed 1"));
        Assert.False(overlay.IsOpenElsewhere("Seed 2"), "its own run");
        Assert.False(overlay.IsOpenElsewhere("Seed 3"), "a run nobody has open");

        // Switching runs lets go of the old one.
        app.Open("Seed 3");
        Assert.False(overlay.IsOpenElsewhere("Seed 1"));
    }
}
