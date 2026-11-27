using System.Text.Json;

namespace Cassini.Routing;

internal sealed record DrawCommand(
    string? CommandId, string? Node, long? Revision, int? Seed = null, DateTimeOffset? At = null,
    string? Mode = null, string? Destination = null, bool Accessible = false, string? SubjectId = null);
internal sealed record DrawResult(RouteOption Route, long Revision);
internal sealed record DrawReceipt(DrawCommand Command, DrawResult Result);

internal sealed class DrawJournal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string statePath;
    private Dictionary<string, DrawReceipt> draws;

    internal DrawJournal(string statePath)
    {
        this.statePath = Path.GetFullPath(statePath);
        draws = File.Exists(this.statePath)
            ? JsonSerializer.Deserialize<Dictionary<string, DrawReceipt>>(File.ReadAllText(this.statePath), Json)
                ?? throw new InvalidDataException("Pusty dziennik losowań.")
            : new(StringComparer.Ordinal);
    }

    internal DrawResult? Get(string commandId)
    {
        lock (gate)
            return draws.GetValueOrDefault(commandId)?.Result;
    }

    internal DrawResult Record(DrawCommand command, Func<DrawResult> choose)
    {
        lock (gate)
        {
            if (draws.TryGetValue(command.CommandId!, out var saved))
            {
                if (command != saved.Command)
                    throw new RoutingException(409, "CommandConflict", "Identyfikator losowania był już użyty z innymi danymi.");
                return saved.Result;
            }

            var result = choose();
            var updated = new Dictionary<string, DrawReceipt>(draws, StringComparer.Ordinal)
            {
                [command.CommandId!] = new(command, result)
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                var temporaryPath = statePath + ".tmp";
                using (var stream = File.Create(temporaryPath))
                {
                    JsonSerializer.Serialize(stream, updated, Json);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, statePath, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new RoutingException(503, "DrawJournalUnavailable", "Nie można zapisać wyniku losowania.");
            }

            draws = updated;
            return result;
        }
    }
}
