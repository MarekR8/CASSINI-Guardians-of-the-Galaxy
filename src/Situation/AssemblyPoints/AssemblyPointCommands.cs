using Cassini.Demo.SiteCatalog;

namespace Situation.AssemblyPoints;

internal sealed record SetAssemblyPointCommand(string? CommandId, int? ExpectedVersion, string? IncidentId,
    string? PointId, string? Name, SitePlace? Place, bool? StepFree, string? Status,
    SiteWindow? OperatingWindow, SiteEvidence? Evidence);

internal sealed class AssemblyPointCommands(CatalogStore<AssemblyPoint> store, SituationStore situation)
{
    internal CatalogWrite<AssemblyPoint> SetAssemblyPoint(SetAssemblyPointCommand command)
    {
        var key = AssemblyPoint.Key(command.IncidentId, command.PointId);
        return store.Save(command.CommandId, command.ExpectedVersion, key, command, (_, version) =>
        {
            var name = AssemblyPoint.ValidateIdentity(command.IncidentId, command.PointId, command.Name);
            if (command.StepFree is not { } stepFree)
                throw new SiteCatalogError(400, "InvalidAssemblyPoint", "Specify whether access is step-free.");

            SiteRules.Validate(command.Place, command.OperatingWindow, command.Evidence, command.Status);
            SiteRules.RequireOpenEvidence(command.Status!, command.Evidence!, situation.Read().Revision);

            return new AssemblyPoint(command.IncidentId!, command.PointId!, name, command.Place!, stepFree,
                command.Status!, command.OperatingWindow!, command.Evidence!, version);
        });
    }
}
