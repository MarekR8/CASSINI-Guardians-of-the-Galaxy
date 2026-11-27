using System.Text.Json;

namespace Cassini.Dispatch;

internal sealed record AssistanceRequest(string RequestId, string CaseId, string PickupNode, string State, int Version);
internal sealed record ResponseUnit(string UnitId, string Name, string State, string Node);
internal sealed record TransportRoute(
    long Revision, string Exit, string[] ApproachSegments, string[] OutboundSegments,
    int ApproachSeconds, int OutboundSeconds);

internal sealed record Assignment(
    string AssignmentId, string RequestId, string CaseId, string UnitId, string State, int Version,
    TransportRoute Route, string LastKnownNode, bool? RouteCurrent = null)
{
    public bool Active => State is not ("Completed" or "Cancelled");
}

internal sealed record DispatchView(AssistanceRequest[] Requests, ResponseUnit[] Units, Assignment[] Assignments);
internal sealed record Receipt(string Command, JsonElement Result);
internal sealed record DispatchState(DispatchView View, Dictionary<string, Receipt> Receipts);

internal sealed class DispatchStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string? path;
    private DispatchState state;

    public DispatchStore(string? dataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
            path = Path.Combine(dataDirectory, "dispatch.json");
        }

        state = path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<DispatchState>(File.ReadAllBytes(path), Json)
                ?? throw new InvalidDataException("Invalid Dispatch demo state.")
            : new DispatchState(new DispatchView(
                [
                    new("request-15", "case-15", "pickup", "Open", 1),
                    new("request-16", "case-16", "pickup", "Open", 1),
                ],
                [new("unit-3", "Jednostka 3", "Available", "exit-east")], []), []);
    }

    public DispatchView Read()
    {
        lock (gate)
            return state.View;
    }

    public IResult? CheckReceipt<TCommand>(string? id, TCommand command)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            return Error("CommandIdRequired", "Provide a command ID of at most 128 characters.", 400);

        var identity = Identify(command);
        lock (gate)
            return CheckReceipt(id, identity);
    }

    public IResult Execute<TCommand>(
        string? id, TCommand command, Func<DispatchView, (DispatchView? Changed, object Payload, int Status)> apply)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            return Error("CommandIdRequired", "Provide a command ID of at most 128 characters.", 400);

        var identity = Identify(command);
        lock (gate)
        {
            if (CheckReceipt(id, identity) is { } previous)
                return previous;

            var (changed, payload, status) = apply(state.View);
            if (changed is null)
                return Results.Json(payload, statusCode: status);

            var result = JsonSerializer.SerializeToElement(payload, Json);
            var receipts = new Dictionary<string, Receipt>(state.Receipts) { [id] = new(identity, result) };
            var next = new DispatchState(changed, receipts);
            try
            {
                if (path is not null)
                {
                    var temporaryPath = path + ".tmp";
                    File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(next, Json));
                    File.Move(temporaryPath, path, overwrite: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Error("StateUnavailable", "Dispatch could not write its local state.", 503);
            }

            state = next;
            return Results.Ok(result);
        }
    }

    private IResult? CheckReceipt(string id, string identity)
    {
        if (!state.Receipts.TryGetValue(id, out var receipt))
            return null;

        return receipt.Command == identity
            ? Results.Ok(receipt.Result)
            : Error("CommandIdConflict", "This command ID was used for another request.", 409);
    }

    private static string Identify<TCommand>(TCommand command) =>
        typeof(TCommand).Name + ":" + JsonSerializer.Serialize(command, Json);

    internal static IResult Error(string code, string detail, int status) =>
        Results.Json(new { code, detail }, statusCode: status);
}
