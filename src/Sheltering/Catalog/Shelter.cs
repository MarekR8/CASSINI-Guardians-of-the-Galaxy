using Cassini.Demo.SiteCatalog;
using Cassini.Sheltering.Reception;

namespace Cassini.Sheltering.Catalog;

internal sealed record ShelterResources(int Places, int AccessiblePlaces, int? IntakePerHour)
{
    internal bool IsValid => Places >= 0 && AccessiblePlaces >= 0 && AccessiblePlaces <= Places &&
        IntakePerHour is null or > 0;
}

internal sealed record ShelterCommitments(
    int HeldPlaces, int HeldAccessiblePlaces, int OccupiedPlaces, int OccupiedAccessiblePlaces)
{
    internal static ShelterCommitments Empty { get; } = new(0, 0, 0, 0);

    internal bool IsValid => HeldPlaces >= 0 && HeldAccessiblePlaces >= 0 && HeldAccessiblePlaces <= HeldPlaces &&
        OccupiedPlaces >= 0 && OccupiedAccessiblePlaces >= 0 && OccupiedAccessiblePlaces <= OccupiedPlaces;
    internal long TotalPlaces => (long)HeldPlaces + OccupiedPlaces;
    internal long TotalAccessiblePlaces => (long)HeldAccessiblePlaces + OccupiedAccessiblePlaces;
}

internal sealed record AvailablePlaces(int Places, int AccessiblePlaces);

internal sealed record Shelter(
    string ShelterId, string Name, SitePlace Place, bool StepFree, string Status, SiteWindow OperatingWindow,
    SiteEvidence Evidence, int Version, ShelterResources Resources, ShelterCommitments Commitments,
    ShelterReception? Reception = null)
{
    internal ShelterReception Ledger => Reception ?? ShelterReception.Empty;

    internal bool IsOvercommitted => Commitments.TotalPlaces > Resources.Places ||
        Commitments.TotalAccessiblePlaces > Resources.AccessiblePlaces;

    internal AvailablePlaces CalculateAvailability()
    {
        var places = (int)Math.Max(0, Resources.Places - Commitments.TotalPlaces);
        var accessible = (int)Math.Max(0, Resources.AccessiblePlaces - Commitments.TotalAccessiblePlaces);
        return new AvailablePlaces(places, Math.Min(places, accessible));
    }
}
