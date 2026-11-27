namespace Cassini.Management;

internal sealed record RecordAlert(string? CommandId, DateTimeOffset? ObservedAt);
internal sealed record RecordProgress(
    string? CommandId, string? CaseId, string? Step, int? ExpectedVersion,
    string? Node, DateTimeOffset? ObservedAt);

internal sealed class IncidentQueries(ManagementStore store)
{
    public IncidentView GetIncident() => store.Read();

    public EvacuationCase? GetCase(string id) =>
        store.Read().Cases.FirstOrDefault(item => item.CaseId == id);
}

internal sealed class IncidentCommands(ManagementStore store)
{
    private static readonly HashSet<string> DemoNodes =
        ["walk-start", "pickup", "east-junction", "exit-east", "north-junction", "exit-north"];

    public IResult RecordAlert(RecordAlert command) => store.Execute(command.CommandId, command, incident =>
    {
        var alert = new HazardAlert(
            $"alert-{incident.Alerts.Length + 1}", command.ObservedAt ?? DateTimeOffset.UtcNow, "synthetic");
        return (incident with { Alerts = [.. incident.Alerts, alert] }, alert, 200);
    });

    public IResult RecordProgress(RecordProgress command) => store.Execute(command.CommandId, command, incident =>
    {
        if (string.IsNullOrWhiteSpace(command.CaseId) || command.ExpectedVersion is null ||
            command.Step is not ("acknowledgement" or "moving" or "exit-confirmed" or "position"))
            return Reject("InvalidProgress", "Provide a case, supported step and expected version.", 400);

        var index = Array.FindIndex(incident.Cases, item => item.CaseId == command.CaseId);
        if (index < 0)
            return Reject("CaseNotFound", "Unknown demo case.", 404);

        var item = incident.Cases[index];
        if (item.Version != command.ExpectedVersion)
            return Reject("VersionConflict", "Read the current case version and retry with a new command ID.", 409);

        if (command.Step == "position")
        {
            if (command.Node is null || !DemoNodes.Contains(command.Node))
                return Reject("UnknownPosition", "Use a confirmed demo graph node.", 400);

            var observedAt = command.ObservedAt ?? DateTimeOffset.UtcNow;
            if (item.PositionObservedAt >= observedAt)
                return Reject("StalePosition", "The case already has a position observed at or after this time.", 409);

            return Update(incident, index, item with
            {
                Node = command.Node,
                PositionObservedAt = observedAt,
                Version = item.Version + 1,
            });
        }

        var progress = (item.Progress, command.Step) switch
        {
            ("Open", "acknowledgement") => "Acknowledged",
            ("Acknowledged", "moving") => "Moving",
            ("Moving", "exit-confirmed") => "ExitConfirmed",
            _ => null,
        };

        return progress is not null
            ? Update(incident, index, item with { Progress = progress, Version = item.Version + 1 })
            : Reject("InvalidTransition", "The requested progress step does not follow the current state.", 409);
    });

    private static (IncidentView, object, int) Update(IncidentView incident, int index, EvacuationCase item)
    {
        var cases = (EvacuationCase[])incident.Cases.Clone();
        cases[index] = item;
        return (incident with { Cases = cases }, item, 200);
    }

    private static (IncidentView?, object, int) Reject(string code, string detail, int status) =>
        (null, new { code, detail }, status);
}
