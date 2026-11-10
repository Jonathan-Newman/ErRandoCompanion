namespace ErRandoCompanion.Core.Models;

/// <summary>
/// A place locations belong to, or a named route that requirements refer to
/// with "route:id". A route is simply a region that no location belongs to.
/// </summary>
public sealed class Region
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>OR of ANDs. Empty means always reachable.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Requires { get; set; } = [];
}
