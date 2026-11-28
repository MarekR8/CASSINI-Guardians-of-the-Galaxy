using Cassini.Demo.SiteCatalog;

namespace Situation.AssemblyPoints;

internal static class AssemblyPointEndpoints
{
    internal static void AddAssemblyPoints(this IServiceCollection services, string seedFile, string? stateFile)
    {
        services.AddSingleton(new CatalogStore<AssemblyPoint>(stateFile, () => ReadSeed(seedFile),
            point => AssemblyPoint.Key(point.IncidentId, point.PointId), point => point.Version));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AssemblyPointCommands>();
        services.AddSingleton<AssemblyPointQueries>();
    }

    internal static void MapAssemblyPoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(string.Empty).AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (SiteCatalogError error)
            {
                return Results.Problem(statusCode: error.Status, title: error.Code, detail: error.Message,
                    extensions: new Dictionary<string, object?> { ["code"] = error.Code });
            }
        });

        group.MapGet("/queries/assembly-points", (string? incidentId, DateTimeOffset? at, bool? eligibleOnly,
            AssemblyPointQueries queries) => queries.GetAssemblyPoints(incidentId, at, eligibleOnly == true));
        group.MapPost("/commands/set-assembly-point", (SetAssemblyPointCommand command, AssemblyPointCommands commands)
            => commands.SetAssemblyPoint(command));
    }

    private static AssemblyPoint[] ReadSeed(string path)
    {
        var points = SiteSeed.Read<AssemblyPoint>(path, "assemblyPoints");
        foreach (var point in points)
        {
            AssemblyPoint.ValidateIdentity(point.IncidentId, point.PointId, point.Name);
            SiteRules.Validate(point.Place, point.OperatingWindow, point.Evidence, point.Status);
        }

        return points;
    }
}
