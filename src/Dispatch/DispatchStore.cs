using System.Text.Json;

namespace Cassini.Dispatch;

internal sealed record AssistanceRequest(string RequestId, string CaseId, string PickupNode, string State, int Version);
internal sealed record ResponseUnit(string UnitId, string Name, string State, string Node,
    PositionEvidence? PositionEvidence = null, bool? PositionVerified = null);
internal sealed record TransportRoute(
    long Revision, string Exit, string[] ApproachSegments, string[] OutboundSegments,
    int ApproachSeconds, int OutboundSeconds, double[][]? ApproachGeometry = null,
    double[][]? OutboundGeometry = null, RouteProvenance? Provenance = null);
internal sealed record RouteProvenance(string Source, string Mode, DateTimeOffset ObservedAt);
internal sealed record DispatchDecision(string Kind, DateTimeOffset At, string CommandId, long Revision,
    string? Node = null, string? Reason = null, string[]? Segments = null);

internal sealed record Assignment(
    string AssignmentId, string RequestId, string CaseId, string UnitId, string State, int Version,
    TransportRoute Route, string LastKnownNode, bool? RouteCurrent = null,
    PositionEvidence? PositionEvidence = null, RouteStatus? RouteStatus = null, DispatchDecision[]? History = null)
{
    public bool Active => State is not ("Completed" or "Cancelled");
}

internal sealed record DispatchView(AssistanceRequest[] Requests, ResponseUnit[] Units, Assignment[] Assignments);
internal sealed record Receipt(string Command, JsonElement Result);
internal sealed record DispatchState(DispatchView View, Dictionary<string, Receipt> Receipts);

internal sealed class DispatchStore(IDispatchPersistence persistence)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DispatchView Read() => persistence.Read().View;

    public IResult? CheckReceipt<TCommand>(string? id, TCommand command)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            return InvalidCommandId();

        return CheckReceipt(persistence.Read(), id, Identify(command));
    }

    public IResult Execute<TCommand>(
        string? id, TCommand command, Func<DispatchView, (DispatchView? Changed, object Payload, int Status)> apply)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            return InvalidCommandId();

        var identity = Identify(command);
        return persistence.Update(state =>
        {
            if (CheckReceipt(state, id, identity) is { } previous)
                return (null, previous);

            var (changed, payload, status) = apply(state.View);
            if (changed is null)
                return (null, Results.Json(payload, statusCode: status));

            var result = JsonSerializer.SerializeToElement(payload, Json);
            var receipts = new Dictionary<string, Receipt>(state.Receipts) { [id] = new(identity, result) };
            return (new DispatchState(changed, receipts), Results.Ok(result));
        });
    }

    internal static DispatchState InitialState(string? dataDirectory)
    {
        var path = StatePath(dataDirectory);
        if (File.Exists(path))
            return JsonSerializer.Deserialize<DispatchState>(File.ReadAllBytes(path), Json)
                ?? throw new InvalidDataException("Invalid Dispatch demo state.");

        return new DispatchState(new DispatchView(
            [
                new("request-15", "case-15", "pickup", "Open", 1),
                new("request-16", "case-16", "pickup", "Open", 1),
            ],
            [new("unit-3", "Jednostka 3", "Available", "exit-east"),
             new("unit-4", "Jednostka 4", "Available", "exit-east")], []), []);
    }

    internal static string? StatePath(string? dataDirectory) => string.IsNullOrWhiteSpace(dataDirectory)
        ? null : Path.Combine(dataDirectory, "dispatch.json");

    private static IResult? CheckReceipt(DispatchState state, string id, string identity)
    {
        if (!state.Receipts.TryGetValue(id, out var receipt))
            return null;

        return receipt.Command == identity
            ? Results.Ok(receipt.Result)
            : Error("CommandIdConflict", "This command ID was used for another request.", 409);
    }

    private static string Identify<TCommand>(TCommand command) =>
        typeof(TCommand).Name + ":" + JsonSerializer.Serialize(command, Json);

    private static IResult InvalidCommandId() =>
        Error("CommandIdRequired", "Provide a command ID of at most 128 characters.", 400);

    internal static IResult Error(string code, string detail, int status) =>
        Results.Json(new { code, detail }, statusCode: status);
}
