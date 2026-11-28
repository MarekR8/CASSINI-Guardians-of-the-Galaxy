using System.Globalization;
using System.Text.Json;

namespace Cassini.Management;

internal sealed record Provenance(string Source, string Mode, DateTimeOffset ObservedAt);

/// <summary>
/// A stored EWSS broadcast. <c>Delivery</c> states what is known about reception; the mock never claims delivery.
/// </summary>
internal sealed record EwssBroadcast(
    string BroadcastId, string Level, string Text, JsonElement Zone, DateTimeOffset IssuedAt,
    string Delivery, Provenance Provenance);

internal sealed record EwssMessage(string Level, string Text, JsonElement Zone);

internal sealed record BroadcastEwssAlert(string? CommandId, string? Level, string? Text, JsonElement? Zone);

/// <summary>Galileo Emergency Warning Satellite Service uplink. There is no public interface, so only a mock exists.</summary>
internal interface IEwssBroadcaster
{
    EwssBroadcast Prepare(string broadcastId, EwssMessage message, DateTimeOffset now);
}

internal sealed class MockEwssBroadcaster : IEwssBroadcaster
{
    public EwssBroadcast Prepare(string broadcastId, EwssMessage message, DateTimeOffset now) =>
        new(broadcastId, message.Level, message.Text, message.Zone, now, "simulated",
            new Provenance("ewss-mock", "mock", now));
}

internal static class EwssMessages
{
    /// <summary>EWSS messages are short; the demo follows the ~100 character budget from task 6.</summary>
    internal const int MaxTextLength = 100;
    internal const int MaxZonePositions = 64;

    /// <summary>CAP-style severity levels.</summary>
    internal static readonly string[] Levels = ["extreme", "severe", "moderate", "minor"];

    internal static (EwssMessage? Message, string? Error) Read(BroadcastEwssAlert command)
    {
        if (!Levels.Contains(command.Level))
            return (null, "Provide level: extreme, severe, moderate or minor.");

        var text = command.Text?.Trim();
        if (string.IsNullOrEmpty(text) || new StringInfo(text).LengthInTextElements > MaxTextLength ||
            text.Any(char.IsControl))
            return (null, $"Provide a single-line text of at most {MaxTextLength} characters.");

        if (command.Zone is not { } zone || !IsPolygon(zone))
            return (null, $"Provide the zone as a GeoJSON Polygon with one closed ring of 4 to {MaxZonePositions} positions.");

        return (new EwssMessage(command.Level!, text, zone.Clone()), null);
    }

    private static bool IsPolygon(JsonElement zone)
    {
        if (zone.ValueKind != JsonValueKind.Object ||
            !zone.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            type.GetString() != "Polygon" ||
            !zone.TryGetProperty("coordinates", out var rings) || rings.ValueKind != JsonValueKind.Array ||
            rings.GetArrayLength() != 1)
            return false;

        var ring = rings[0];
        if (ring.ValueKind != JsonValueKind.Array || ring.GetArrayLength() is < 4 or > MaxZonePositions)
            return false;

        var positions = new List<(double Longitude, double Latitude)>();
        foreach (var position in ring.EnumerateArray())
        {
            if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() != 2 ||
                position[0].ValueKind != JsonValueKind.Number || position[1].ValueKind != JsonValueKind.Number)
                return false;
            var (longitude, latitude) = (position[0].GetDouble(), position[1].GetDouble());
            if (longitude is < -180 or > 180 || latitude is < -90 or > 90)
                return false;
            positions.Add((longitude, latitude));
        }

        return positions[0] == positions[^1];
    }
}

internal sealed class EwssCommands(ManagementStore store, IEwssBroadcaster broadcaster, TimeProvider clock)
{
    public IResult Broadcast(BroadcastEwssAlert command) => store.Execute(command.CommandId, command, state =>
    {
        var (message, error) = EwssMessages.Read(command);
        if (message is null)
            return Outcome.Reject("InvalidEwssAlert", error!, 400);

        var broadcast = broadcaster.Prepare($"ewss-{state.Broadcasts.Length + 1}", message, clock.GetUtcNow());
        return Outcome.Accept(state with { Broadcasts = [.. state.Broadcasts, broadcast] }, broadcast);
    });
}
