using Cassini.Demo.SiteCatalog;
using System.Text.Json;
using Cassini.Sheltering.Catalog;
using Cassini.Sheltering.Reception;

namespace Cassini.Sheltering.Infrastructure;

internal static class ShelterSeed
{
    internal static Shelter[] Read(string path)
    {
        var shelters = SiteSeed.Read<Shelter>(path, "shelters");
        foreach (var shelter in shelters)
        {
            if (shelter is null || !SiteRules.IsId(shelter.ShelterId) || shelter.Name?.Trim() is not { Length: > 0 and <= 120 } ||
                shelter.Version < 1 || shelter.Resources is not { IsValid: true } || shelter.Commitments is not { IsValid: true })
            {
                throw new InvalidDataException($"Invalid initial shelter in '{path}'.");
            }

            try
            {
                SiteRules.Validate(shelter.Place, shelter.OperatingWindow, shelter.Evidence, shelter.Status);
            }
            catch (SiteCatalogError error)
            {
                throw new InvalidDataException($"Invalid site data for shelter '{shelter.ShelterId}' in '{path}'.", error);
            }
        }

        return shelters;
    }

    private sealed record HoldSeed(string? ShelterId, ShelterHold? Hold);

    /// <summary>
    /// Add SH-W4 fixture holds (data/demo/sheltering/sample-holds.json) to the seeded shelters. Each hold adds its places
    /// to the held commitments, so the seed stays within capacity or is rejected.
    /// </summary>
    internal static Shelter[] WithHolds(Shelter[] shelters, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return shelters;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var holds = document.RootElement.GetProperty("holds").Deserialize<HoldSeed[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException($"Missing holds in '{path}'.");
        var result = shelters.ToDictionary(shelter => shelter.ShelterId, StringComparer.Ordinal);
        foreach (var seed in holds)
        {
            if (seed.ShelterId is null || !result.TryGetValue(seed.ShelterId, out var shelter) || seed.Hold is not { } hold ||
                !SiteRules.IsId(hold.ReservationId) || !SiteRules.IsId(hold.IncidentId) || !SiteRules.IsId(hold.CaseId) ||
                !SiteRules.IsId(hold.LegId) || hold.Resources is not { IsValid: true })
                throw new InvalidDataException($"Invalid hold in '{path}'.");
            if (result.Values.Any(other => other.Ledger.Holds.Any(item => item.CaseId == hold.CaseId && item.LegId == hold.LegId)))
                throw new InvalidDataException($"Case {hold.CaseId} holds more than one place for leg {hold.LegId} in '{path}'.");
            var updated = shelter with
            {
                Commitments = shelter.Commitments with
                {
                    HeldPlaces = shelter.Commitments.HeldPlaces + hold.Resources.Places,
                    HeldAccessiblePlaces = shelter.Commitments.HeldAccessiblePlaces + hold.Resources.AccessiblePlaces,
                },
                Reception = shelter.Ledger with { Holds = [.. shelter.Ledger.Holds, hold] },
            };
            if (updated.IsOvercommitted)
                throw new InvalidDataException($"Holds in '{path}' exceed the places of {shelter.ShelterId}.");
            result[shelter.ShelterId] = updated;
        }
        return shelters.Select(shelter => result[shelter.ShelterId]).ToArray();
    }
}
