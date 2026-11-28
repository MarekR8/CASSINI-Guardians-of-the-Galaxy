using System.Text.Json;
using Cassini.Management.Grouping;

namespace Cassini.Management;

internal sealed record HazardAlert(string AlertId, DateTimeOffset ObservedAt, string Source);

internal sealed record EvacuationCase(
    string CaseId, string Name, string Node, string Mobility, string Progress, int Version,
    DateTimeOffset? PositionObservedAt = null);

internal sealed record IncidentView(string IncidentId, HazardAlert[] Alerts, EvacuationCase[] Cases);
internal sealed record Receipt(string Command, JsonElement Result);
internal sealed record ManagementState(
    IncidentView Incident, Dictionary<string, Receipt> Receipts,
    Dictionary<string, EvacuationGroup>? Groups = null);

internal sealed class ManagementStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string? path;
    private ManagementState state;

    public ManagementStore(string? dataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
            path = Path.Combine(dataDirectory, "management.json");
        }

        state = path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<ManagementState>(File.ReadAllBytes(path), Json)
                ?? throw new InvalidDataException("Invalid Management demo state.")
            : new ManagementState(
                new IncidentView("incident-42", [],
                [
                    new("case-14", "Mieszkaniec 14", "walk-start", "Walking", "Open", 1),
                    new("case-15", "Mieszkaniec 15", "pickup", "Assisted", "Open", 1),
                    new("case-16", "Mieszkaniec 16", "pickup", "Assisted", "Open", 1),
                ]), []);
    }

    public IncidentView Read() => ReadSnapshot().Incident;

    public ManagementState ReadSnapshot()
    {
        lock (gate)
            return state;
    }

    public IResult Execute<TCommand>(
        string? id, TCommand command, Func<IncidentView, (IncidentView? Changed, object Payload, int Status)> apply)
        => ExecuteState(id, command, snapshot =>
        {
            var (incident, payload, status) = apply(snapshot.Incident);
            return (incident is null ? null : snapshot with { Incident = incident }, payload, status);
        });

    public IResult ExecuteState<TCommand>(
        string? id, TCommand command, Func<ManagementState, (ManagementState? Changed, object Payload, int Status)> apply)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            return Error("CommandIdRequired", "Provide a command ID of at most 128 characters.", 400);

        var identity = typeof(TCommand).Name + ":" + JsonSerializer.Serialize(command, Json);
        lock (gate)
        {
            if (state.Receipts.TryGetValue(id, out var receipt))
                return receipt.Command == identity
                    ? Results.Ok(receipt.Result)
                    : Error("CommandIdConflict", "This command ID was used for another request.", 409);

            var (changed, payload, status) = apply(state);
            if (changed is null)
                return Results.Json(payload, statusCode: status);

            var result = JsonSerializer.SerializeToElement(payload, Json);
            var receipts = new Dictionary<string, Receipt>(state.Receipts) { [id] = new(identity, result) };
            var next = changed with { Receipts = receipts };
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
                return Error("StateUnavailable", "Management could not write its local state.", 503);
            }

            state = next;
            return Results.Ok(result);
        }
    }

    private static IResult Error(string code, string detail, int status) =>
        Results.Json(new { code, detail }, statusCode: status);
}
