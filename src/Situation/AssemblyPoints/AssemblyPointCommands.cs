using Cassini.Demo.SiteCatalog;
using Cassini.Messaging;

namespace Situation.AssemblyPoints;

internal sealed record SetAssemblyPointCommand(string? CommandId, int? ExpectedVersion, string? IncidentId,
    string? PointId, string? Name, SitePlace? Place, bool? StepFree, string? Status,
    SiteWindow? OperatingWindow, SiteEvidence? Evidence);

internal sealed class AssemblyPointCommands(CatalogStore<AssemblyPoint> store, SituationStore situation, TimeProvider clock)
{
    internal CatalogWrite<AssemblyPoint> SetAssemblyPoint(SetAssemblyPointCommand command, CatalogActor actor)
    {
        var key = AssemblyPoint.Key(command.IncidentId, command.PointId);
        return store.Save(command.CommandId, command.ExpectedVersion, key, command, actor, clock.GetUtcNow(), (_, version) =>
        {
            var name = AssemblyPoint.ValidateIdentity(command.IncidentId, command.PointId, command.Name);
            if (command.StepFree is not { } stepFree)
                throw new SiteCatalogError(400, "InvalidAssemblyPoint", "Specify whether access is step-free.");

            SiteRules.Validate(command.Place, command.OperatingWindow, command.Evidence, command.Status);
            SiteRules.RequireOpenEvidence(command.Status!, command.Evidence!, situation.Read().Revision);

            return new AssemblyPoint(command.IncidentId!, command.PointId!, name, command.Place!, stepFree,
                command.Status!, command.OperatingWindow!, command.Evidence!, version);
        }, (_, point) => new(EventTypes.AssemblyPointChanged, EventTypes.RoutingKey(EventTypes.AssemblyPointChanged), point.PointId, new
        {
            point.IncidentId,
            point.PointId,
            point.Status,
            point.OperatingWindow,
            evidenceRef = new { point.Evidence.Source, point.Evidence.SituationRevision },
        }));
    }
}
