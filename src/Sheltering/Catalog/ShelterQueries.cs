using Cassini.Demo.SiteCatalog;
using Cassini.Sheltering.Allocation;
using Cassini.Sheltering.Infrastructure;

namespace Cassini.Sheltering.Catalog;

internal sealed record ShelterAvailabilityView(
    string ShelterId, string Name, SitePlace Place, bool StepFree, string Status, SiteWindow OperatingWindow,
    SiteEvidence Evidence, int Version, ShelterResources Resources, ShelterCommitments Commitments,
    AvailablePlaces Available, bool Overcommitted, bool Eligible, string? IneligibleReason);

// Commitments combine the configured baseline with active holds; AllocationRevision identifies the holds read.
internal sealed record ShelterCatalogView(int Revision, int? SituationRevision, DateTimeOffset At,
    ShelterAvailabilityView[] Shelters, int AllocationRevision);

internal sealed class ShelterQueries(CatalogStore<Shelter> catalog, AllocationStore allocations, SituationClient situation,
    TimeProvider clock)
{
    internal async Task<ShelterCatalogView> GetShelterAvailability(DateTimeOffset? at, bool eligibleOnly,
        CancellationToken cancellation)
    {
        var checkedAt = (at ?? clock.GetUtcNow()).ToUniversalTime();
        var situationRevision = await situation.GetRevision(cancellation);
        if (eligibleOnly && situationRevision is null)
            throw SituationClient.Unavailable();

        var (revision, shelters, allocation) = allocations.Exclusive(() =>
        {
            var (catalogRevision, items) = catalog.Read();
            return (catalogRevision, items, allocations.Read());
        });
        var results = shelters.OrderBy(shelter => shelter.ShelterId, StringComparer.Ordinal)
            .Select(shelter => Evaluate(AllocationQueries.WithAllocations(allocation, shelter), checkedAt, situationRevision));
        if (eligibleOnly)
            results = results.Where(shelter => shelter.Eligible);
        return new ShelterCatalogView(revision, situationRevision, checkedAt, results.ToArray(), allocation.Revision);
    }

    private static ShelterAvailabilityView Evaluate(Shelter shelter, DateTimeOffset at, int? situationRevision)
    {
        var free = shelter.CalculateAvailability();
        var reason = SiteRules.IneligibleReason(shelter.Status, shelter.OperatingWindow, shelter.Evidence, at,
            situationRevision);
        if (reason is null)
            reason = shelter.IsOvercommitted ? "Overcommitted" : free.Places == 0 ? "NoAvailablePlaces" : null;

        return new ShelterAvailabilityView(shelter.ShelterId, shelter.Name, shelter.Place, shelter.StepFree, shelter.Status,
            shelter.OperatingWindow, shelter.Evidence, shelter.Version, shelter.Resources, shelter.Commitments,
            free, shelter.IsOvercommitted, reason is null, reason);
    }
}
