using Cassini.Demo.SiteCatalog;
using Cassini.Sheltering.Catalog;

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
}
