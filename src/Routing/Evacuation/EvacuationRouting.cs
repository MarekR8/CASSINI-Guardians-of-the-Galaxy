namespace Cassini.Routing.Evacuation;

internal sealed record EvacuationOption(
    string Id, string Exit, string Name, int Seconds, double Weight, double ExpectedLoad, int Capacity,
    bool StepFree, string[] Segments, string[] Nodes, DateTimeOffset DepartsAt, DateTimeOffset ArrivesAt);

internal sealed record EvacuationDistribution(
    string Node, long Revision, string Model, string Profile, string Status, int Wave, double StartMinute,
    double DeadlineMinutes, double? LeaveFloodMinutes, double People, EvacuationOption[] Options);

/// <summary>Valencia queries and the draw command on top of the task 03 evacuation model.</summary>
internal sealed class EvacuationRouting(SituationGateway situation, DrawJournal journal, EvacuationPlanner planner)
{
    internal const string ModelName = "logit-sue";
    private const double MinimumWeight = 1e-6;

    internal async Task<object> PlanAsync(CancellationToken cancellationToken)
    {
        var plan = planner.Require(await situation.ReadAsync(cancellationToken));
        var result = plan.Result;
        return new
        {
            plan.Revision, model = ModelName, plan.ComputedAt, plan.DurationSeconds, closedSegments = plan.Closed,
            result.ComplianceShare,
            people = new
            {
                estimated = result.PeopleEstimated, handedOver = result.PeopleHandedOver,
                guided = result.Status.Values.Sum(), uninformed = result.PeopleUninformed, kind = "estimate",
            },
            result.Status, result.StatusAll, uninformedStatus = result.UninformedStatus, result.Shelters, result.Waves,
            profiles = plan.Model.Profiles.Select(profile => new { profile.Name, profile.Share }),
        };
    }

    internal async Task<EvacuationDistribution> DistributionAsync(string node, string? profile, CancellationToken cancellationToken)
    {
        var snapshot = await situation.ReadAsync(cancellationToken);
        return Build(planner.Require(snapshot), node, profile);
    }

    internal async Task<DrawResult> DrawAsync(DrawCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.CommandId) || command.CommandId.Length > 120 ||
            command.SubjectId?.Length > 120 || command.Revision is null or < 0 || command.Node is null)
            throw new RoutingException(400, "InvalidRequest", "Podaj identyfikator losowania, węzeł i wersję warunków.");
        var snapshot = await situation.ReadAsync(cancellationToken);
        if (command.Revision != snapshot.Revision)
            throw new RoutingException(409, "RevisionConflict", "Warunki zmieniły się. Pobierz aktualny rozkład tras.");
        var plan = planner.Require(snapshot);
        var distribution = Build(plan, command.Node, command.Mode);
        if (distribution.Status != "Ok")
            throw new RoutingException(422, distribution.Status, distribution.Status switch
            {
                "DeadlineInfeasible" => "Z tego węzła nie da się opuścić strefy przed terminem. Sprawa wymaga eskalacji.",
                "ShelterCapacityExhausted" => "W dostępnych schronach nie ma już miejsc dla tej grupy.",
                _ => "Z tego węzła nie prowadzi żadna dopuszczalna trasa do schronu.",
            });

        return journal.Record(command, () =>
        {
            var chance = command.Seed is { } seed ? new Random(seed).NextDouble() : Random.Shared.NextDouble();
            var cumulative = 0.0;
            var chosen = distribution.Options[^1];
            foreach (var option in distribution.Options)
            {
                cumulative += option.Weight;
                if (chance < cumulative)
                {
                    chosen = option;
                    break;
                }
            }
            return new DrawResult(new RouteOption(chosen.Id, chosen.Exit, chosen.Seconds, chosen.Weight, chosen.Segments,
                chosen.Nodes, chosen.DepartsAt, chosen.ArrivesAt), distribution.Revision);
        });
    }

    private static EvacuationDistribution Build(EvacuationPlan plan, string node, string? profile)
    {
        profile ??= plan.Model.Profiles[0].Name;
        if (plan.Model.Profiles.All(item => item.Name != profile))
            throw new RoutingException(400, "InvalidProfile", "Nieznany profil mobilności: " +
                string.Join(", ", plan.Model.Profiles.Select(item => item.Name)) + ".");
        if (!long.TryParse(node, out var nodeId) || !plan.Model.Index.ContainsKey(nodeId))
            throw new RoutingException(400, "InvalidNode", "Podaj identyfikator węzła OSM z grafu ewakuacji.");
        if (!plan.Result.Nodes.TryGetValue(nodeId, out var groups))
            throw new RoutingException(404, "NodeNotPlanned", "Dla tego węzła nie ma szacunku ludności w strefie zalania.");
        var group = groups[profile];
        var departs = RoutingQueries.StartTime.AddMinutes(group.StartMinute);
        var options = new List<EvacuationOption>();
        if (group.Shares is { } shares)
        {
            for (var row = 0; row < shares.Length; row++)
            {
                if (shares[row] < MinimumWeight || plan.Model.Route(nodeId, profile, row, group.Wave) is not { } route)
                    continue;
                var shelter = plan.Model.Shelters[row];
                var seconds = (int)Math.Round(group.RouteSeconds[row]);
                options.Add(new($"{node}:{shelter.Id}", shelter.Id, shelter.Name, seconds, shares[row],
                    plan.Result.Shelters[row].ExpectedLoad, shelter.Capacity, shelter.StepFree, route.Segments,
                    route.Nodes.Select(id => id.ToString()).ToArray(), departs, departs.AddSeconds(seconds)));
            }
            var total = options.Sum(option => option.Weight);
            options = options.Select(option => option with { Weight = option.Weight / total }).ToList();
        }
        return new(node, plan.Revision, ModelName, profile, group.Status, group.Wave, group.StartMinute,
            group.DeadlineMinutes, group.LeaveFloodMinutes, group.People, options.ToArray());
    }
}
