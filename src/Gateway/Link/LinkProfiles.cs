namespace Cassini.Gateway.Link;

/// <summary>
/// A simulated link between field clients and the coordination point. Latency is added once per command
/// (round trip) and throughput delays the request body. Values are demo assumptions, not a specification.
/// </summary>
internal sealed record LinkProfile(string Name, bool Available, int RoundTripMs, int ThroughputKbps, string Basis)
{
    internal TimeSpan TransitTime(long bytes) => !Available
        ? TimeSpan.Zero
        : TimeSpan.FromMilliseconds(RoundTripMs + bytes * 8.0 / ThroughputKbps);
}

internal static class LinkProfiles
{
    internal static readonly LinkProfile Terrestrial = new("terrestrial", true, 10, 100_000,
        "Demo assumption for a local terrestrial network.");
    internal static readonly LinkProfile Iris2 = new("iris2", true, 40, 20_000,
        "Simulated IRIS² LEO link; round trip from the 30–50 ms LEO range in task 6. No public IRIS² API exists.");
    internal static readonly LinkProfile Govsatcom = new("govsatcom", true, 600, 2_000,
        "Simulated GOVSATCOM GEO link; round trip ~600 ms (task 6). Profile only.");
    internal static readonly LinkProfile Down = new("down", false, 0, 1, "No link: field commands wait in the queue.");

    internal static readonly LinkProfile[] All = [Terrestrial, Iris2, Govsatcom, Down];

    internal static LinkProfile? Find(string? name) => All.FirstOrDefault(profile => profile.Name == name);
}
