namespace Cassini.Management;

internal sealed record RecordAlert(string? CommandId, DateTimeOffset? ObservedAt);

internal sealed class IncidentCommands(ManagementStore store, TimeProvider clock)
{
    private static readonly HashSet<string> DemoNodes =
        ["walk-start", "pickup", "east-junction", "exit-east", "north-junction", "exit-north"];

    private static readonly HashSet<string> Steps =
        ["acknowledgement", "moving", "position", "pickup", "exit-confirmed"];

    public IResult RecordAlert(RecordAlert command) => store.Execute(command.CommandId, command, state =>
    {
        var incident = state.Incident;
        var alert = new HazardAlert(
            $"alert-{incident.Alerts.Length + 1}", command.ObservedAt ?? clock.GetUtcNow(), "synthetic");
        return Outcome.Accept(state with { Incident = incident with { Alerts = [.. incident.Alerts, alert] } }, alert);
    });

    public IResult RecordProgress(RecordProgress command) => store.Execute(command.CommandId, command, state =>
    {
        // A late field exit report may not know the current case version; its transition rules protect it instead.
        var versionOptional = command.Step == "exit-confirmed";
        if (string.IsNullOrWhiteSpace(command.CaseId) || command.Step is null || !Steps.Contains(command.Step) ||
            (command.ExpectedVersion is null && !versionOptional))
            return Outcome.Reject("InvalidProgress", "Provide a case, supported step and expected version.", 400);

        var incident = state.Incident;
        var index = Array.FindIndex(incident.Cases, item => item.CaseId == command.CaseId);
        if (index < 0)
            return Outcome.Reject("CaseNotFound", "Unknown demo case.", 404);

        var item = incident.Cases[index];
        if (command.ExpectedVersion is { } expected && item.Version != expected)
            return Outcome.Reject("VersionConflict", "Read the current case version and retry with a new command ID.", 409);

        PositionFix? fix = null;
        if (FieldEvidence.HasFix(command) && command.Step is not ("pickup" or "exit-confirmed"))
            return Outcome.Reject("InvalidProgress", "Position evidence belongs to pickup or exit-confirmed.", 400);
        if (command.Step == "exit-confirmed" || FieldEvidence.HasFix(command))
        {
            var (read, error) = FieldEvidence.Read(command, clock.GetUtcNow());
            if (read is null)
                return Outcome.Reject("PositionEvidenceRequired", error!, 400);
            fix = read;
        }

        return command.Step switch
        {
            "position" => RecordPosition(state, index, item, command),
            "pickup" => Transition(item, ProgressStates.PickedUp,
                ProgressStates.Open, ProgressStates.Acknowledged, ProgressStates.Moving) is { } picked
                ? Accept(state, index, picked with { Pickup = fix }) : InvalidTransition(item),
            "exit-confirmed" => Transition(item, ProgressStates.ExitConfirmed,
                ProgressStates.Open, ProgressStates.Acknowledged, ProgressStates.Moving, ProgressStates.PickedUp) is { } exited
                ? Accept(state, index, exited with { Exit = fix }) : InvalidTransition(item),
            "acknowledgement" => Transition(item, ProgressStates.Acknowledged, ProgressStates.Open) is { } acknowledged
                ? Accept(state, index, acknowledged) : InvalidTransition(item),
            _ => Transition(item, ProgressStates.Moving, ProgressStates.Acknowledged) is { } moving
                ? Accept(state, index, moving) : InvalidTransition(item),
        };
    });

    private Outcome RecordPosition(ManagementState state, int index, EvacuationCase item, RecordProgress command)
    {
        if (command.Node is null || !DemoNodes.Contains(command.Node))
            return Outcome.Reject("UnknownPosition", "Use a confirmed demo graph node.", 400);

        var observedAt = command.ObservedAt ?? clock.GetUtcNow();
        if (item.PositionObservedAt >= observedAt)
            return Outcome.Reject("StalePosition", "The case already has a position observed at or after this time.", 409);

        return Accept(state, index, item with
        {
            Node = command.Node,
            PositionObservedAt = observedAt,
            Version = item.Version + 1,
        });
    }

    private static EvacuationCase? Transition(EvacuationCase item, string target, params string[] from) =>
        from.Contains(item.Progress) ? item with { Progress = target, Version = item.Version + 1 } : null;

    private static Outcome InvalidTransition(EvacuationCase item) => item.Progress == ProgressStates.ExitConfirmed
        ? Outcome.Reject("ExitAlreadyConfirmed", "This case already has a confirmed exit.", 409)
        : Outcome.Reject("InvalidTransition", "The requested progress step does not follow the current state.", 409);

    private static Outcome Accept(ManagementState state, int index, EvacuationCase item)
    {
        var cases = (EvacuationCase[])state.Incident.Cases.Clone();
        cases[index] = item;
        return Outcome.Accept(state with { Incident = state.Incident with { Cases = cases } }, item);
    }
}
