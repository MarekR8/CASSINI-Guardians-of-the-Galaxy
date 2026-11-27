namespace Cassini.Routing;

internal sealed class RoutingCommands(SituationGateway situation, DrawJournal journal)
{
    internal async Task<DrawResult> DrawAsync(DrawCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.CommandId) || command.CommandId.Length > 120 ||
            command.SubjectId?.Length > 120 || command.Revision is null or < 0)
            throw new RoutingException(400, "InvalidRequest", "Podaj identyfikator losowania i wersję warunków.");
        RoutingQueries.ValidateDistribution(command.Node, command.Mode, command.Destination);

        var snapshot = await situation.ReadAsync(cancellationToken);
        RequireRevision(command.Revision.Value, snapshot);

        var result = journal.Record(command, () => DrawFromDistribution(command, snapshot));
        var latest = await situation.ReadAsync(cancellationToken);
        RequireRevision(result.Revision, latest);
        return result;
    }

    private static void RequireRevision(long revision, SituationSnapshot snapshot)
    {
        if (revision != snapshot.Revision)
            throw new RoutingException(409, "RevisionConflict", "Warunki zmieniły się. Pobierz aktualny rozkład tras.");
    }

    private static DrawResult DrawFromDistribution(DrawCommand command, SituationSnapshot snapshot)
    {
        var distribution = RoutingQueries.BuildDistribution(snapshot, command.Node!, command.At, command.Mode,
            command.Destination, command.Accessible);
        if (distribution.Options.Length == 0)
            throw new RoutingException(422, "NoEligibleRoute", "W tym węźle nie ma dostępnej trasy w podanym czasie.");

        var chance = command.Seed is { } seed ? new Random(seed).NextDouble() : Random.Shared.NextDouble();
        var cumulative = 0.0;
        foreach (var route in distribution.Options)
        {
            cumulative += route.Weight;
            if (chance < cumulative)
                return new(route, distribution.Revision);
        }

        // Floating-point addition can leave the sum just below one.
        return new(distribution.Options[^1], distribution.Revision);
    }
}
