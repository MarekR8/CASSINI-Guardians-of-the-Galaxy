using System.Text.Json.Serialization;
using Cassini.Demo.SiteCatalog;
using Cassini.Sheltering.Infrastructure;
using Cassini.Messaging;

namespace Cassini.Sheltering.Catalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfigureShelterCommand(
    string? CommandId, int? ExpectedVersion, string? ShelterId, string? Name, SitePlace? Place, bool? StepFree,
    string? Status, SiteWindow? OperatingWindow, SiteEvidence? Evidence, ConfigureShelterResources? Resources);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfigureShelterResources(int? Places, int? AccessiblePlaces, int? IntakePerHour);

internal sealed class ShelterCommands(CatalogStore<Shelter> catalog, SituationClient situation, TimeProvider clock)
{
    internal async Task<CatalogWrite<Shelter>> ConfigureShelter(ConfigureShelterCommand command, CatalogActor actor,
        string incidentId, CancellationToken cancellation)
    {
        // Resolve evidence outside the catalog lock. Examinations and failures run in the callback so a
        // retry can still return its prior receipt if the Situation revision changed or became unavailable.
        var situationRevision = actor.CanConfigureSite && command.Status == "open"
            ? await situation.GetRevision(cancellation) : null;
        return catalog.Save(command.CommandId, command.ExpectedVersion, command.ShelterId ?? string.Empty, command,
            actor, clock.GetUtcNow(), (current, version) => Apply(command, current, version, situationRevision, actor),
            (previous, shelter) => DescribeChange(incidentId, previous, shelter));
    }

    private static Shelter Apply(ConfigureShelterCommand command, Shelter? current, int version, int? situationRevision,
        CatalogActor actor)
    {
        if (!SiteRules.IsId(command.ShelterId) || command.Name?.Trim() is not { Length: > 0 and <= 120 } name ||
            command.StepFree is not { } stepFree)
        {
            throw new SiteCatalogError(400, "InvalidShelter", "Specify a shelter ID, a name and an explicit access flag.");
        }

        if (command.Resources is not { Places: { } places, AccessiblePlaces: { } accessible } resources ||
            new ShelterResources(places, accessible, resources.IntakePerHour) is not { IsValid: true } limits)
        {
            throw new SiteCatalogError(400, "InvalidResources",
                "Specify non-negative place limits, accessible places within the total, and a positive or unlimited intake rate.");
        }

        if (!actor.CanConfigureSite)
        {
            if (current is null || name != current.Name || command.Place != current.Place ||
                stepFree != current.StepFree || command.Status != current.Status ||
                command.OperatingWindow != current.OperatingWindow || command.Evidence != current.Evidence)
                throw SiteCatalogAccess.Forbidden();

            return current with { Version = version, Resources = limits };
        }

        SiteRules.Validate(command.Place, command.OperatingWindow, command.Evidence, command.Status);
        if (command.Status == "open")
        {
            if (situationRevision is not { } revision)
                throw SituationClient.Unavailable();
            SiteRules.RequireOpenEvidence(command.Status, command.Evidence!, revision);
        }

        return new Shelter(command.ShelterId!, name, command.Place!, stepFree, command.Status!, command.OperatingWindow!,
            command.Evidence!, version, limits, current?.Commitments ?? ShelterCommitments.Empty,
            current?.Reception);
    }

    private static SiteChange DescribeChange(string incidentId, Shelter? previous, Shelter shelter)
    {
        var affectedResources = new List<string>();
        if (previous?.Resources.Places != shelter.Resources.Places)
            affectedResources.Add("places");
        if (previous?.Resources.AccessiblePlaces != shelter.Resources.AccessiblePlaces)
            affectedResources.Add("accessiblePlaces");
        if (previous is null || previous.Resources.IntakePerHour != shelter.Resources.IntakePerHour)
            affectedResources.Add("intakePerHour");

        return new(EventTypes.ShelterAvailabilityChanged, EventTypes.RoutingKey(EventTypes.ShelterAvailabilityChanged),
            shelter.ShelterId, new
        {
            incidentId,
            shelter.ShelterId,
            shelter.Status,
            shelter.OperatingWindow,
            affectedResources,
            evidenceRef = new { shelter.Evidence.Source, shelter.Evidence.SituationRevision },
        });
    }
}
