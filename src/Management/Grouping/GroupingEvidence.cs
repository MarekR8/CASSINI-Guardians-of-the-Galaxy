using System.Text.Json;

namespace Cassini.Management.Grouping;

internal sealed record GroupingEvidence(DateTimeOffset ExerciseAt, RouteEvidenceSet[] Evidence)
{
    public static GroupingEvidence Load(string path) =>
        JsonSerializer.Deserialize<GroupingEvidence>(File.ReadAllBytes(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("Missing Grouping route evidence.");
}

internal sealed record RouteEvidenceSet(
    string EvidenceId, string IncidentId, string LegId, int SituationRevision, string Source,
    DateTimeOffset ObservedAt, DateTimeOffset ValidUntil, CaseRouteEvidence[] Cases,
    TravelCandidate[] Candidates, string[][] Options);

internal sealed record CaseRouteEvidence(string CaseId, string Node, int CaseVersion, DateTimeOffset ClearanceDeadline);

internal sealed record TravelCandidate(
    string RouteId, string[] CaseIds, string StartNode, string DestinationKind, string DestinationId, bool Eligible,
    string[] SupportedMobility, DateTimeOffset DepartsAt, DateTimeOffset ClearsHazardAt, DateTimeOffset ArrivesAt);

internal static class TravelPartyEvaluator
{
    public static TravelPartyProposal Evaluate(
        EvacuationGroup group, RouteEvidenceSet evidence, EvacuationCase[] cases, DateTimeOffset now)
    {
        var care = group.Relationships.Where(link => link.Kind == "required-care").ToArray();
        var preferences = group.Relationships.Where(link => link.Kind != "required-care").ToArray();
        var memberEvidence = evidence.Cases.Where(item => group.CaseIds.Contains(item.CaseId)).ToArray();
        TravelPartyProposal Proposal(string status, string reason, TravelCandidate[] parties) =>
            new(group.IncidentId, group.GroupId, group.GroupRevision, evidence.LegId,
                evidence.EvidenceId, evidence.SituationRevision, evidence.Source, evidence.ObservedAt,
                evidence.ValidUntil, status, reason, memberEvidence, care, preferences, parties);

        if (group.Plans.Any(plan => plan.RouteEvidenceInvalidated && plan.Proposal.LegId == evidence.LegId &&
            plan.Proposal.EvidenceId == evidence.EvidenceId))
            return Proposal("ReviewRequired", "RouteEvidenceInvalidated", []);

        if (evidence.ObservedAt > now || evidence.ValidUntil <= now ||
            memberEvidence.Length != group.CaseIds.Length ||
            !group.CaseIds.All(id => memberEvidence.Count(item => item.CaseId == id) == 1) ||
            memberEvidence.Any(item => !cases.Any(person => person.CaseId == item.CaseId &&
                person.Version == item.CaseVersion && person.Node == item.Node)))
            return Proposal("ReviewRequired", "StaleCaseOrRouteEvidence", []);

        var eligible = evidence.Candidates.Where(candidate =>
            candidate.Eligible && candidate.CaseIds.Length > 0 &&
            candidate.DepartsAt >= now && candidate.DepartsAt <= candidate.ClearsHazardAt &&
            candidate.ClearsHazardAt <= candidate.ArrivesAt && candidate.ArrivesAt <= evidence.ValidUntil &&
            candidate.CaseIds.All(id => cases.Any(person => person.CaseId == id &&
                candidate.SupportedMobility.Contains(person.Mobility)) &&
                memberEvidence.Any(item => item.CaseId == id && candidate.ClearsHazardAt <= item.ClearanceDeadline))
        ).ToDictionary(candidate => candidate.RouteId);

        var feasible = evidence.Options
            .Where(option => option.All(eligible.ContainsKey))
            .Select(option => option.Select(id => eligible[id]).OrderBy(candidate => candidate.RouteId, StringComparer.Ordinal).ToArray())
            .Where(parties => parties.SelectMany(party => party.CaseIds).Order(StringComparer.Ordinal)
                .SequenceEqual(group.CaseIds.Order(StringComparer.Ordinal)))
            .ToArray();

        var selected = feasible
            .Where(parties => care.All(link => parties.Any(party =>
                party.CaseIds.Contains(link.CaseId) && party.CaseIds.Contains(link.RelatedCaseId))))
            .OrderByDescending(parties => parties.Length == 1)
            .ThenByDescending(parties => preferences.Count(link => SharesShelter(parties, link)))
            .ThenBy(parties => parties.Length)
            .ThenBy(parties => parties.Sum(party => (party.ArrivesAt - now).TotalSeconds * party.CaseIds.Length))
            .ThenBy(parties => string.Join(",", parties.Select(party => party.RouteId)), StringComparer.Ordinal)
            .FirstOrDefault();

        if (selected is null)
            return Proposal("ReviewRequired", feasible.Length > 0 ? "RequiredCareSplit" : "NoFeasibleTravelParties", []);

        var reason = selected.Length == 1 ? "TravelTogether"
            : preferences.Length == 0 ? "IndependentDepartures"
            : preferences.All(link => SharesShelter(selected, link)) ? "IndependentDeparturesSharedShelter"
            : "SharedShelterNotFeasible";
        return Proposal("Proposed", reason, selected);
    }

    private static bool SharesShelter(TravelCandidate[] parties, GroupRelationship link)
    {
        var left = parties.Single(party => party.CaseIds.Contains(link.CaseId));
        var right = parties.Single(party => party.CaseIds.Contains(link.RelatedCaseId));
        return left.DestinationKind == "shelter" && right.DestinationKind == "shelter" &&
            left.DestinationId == right.DestinationId;
    }
}
