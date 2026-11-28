using System.Security.Cryptography;
using System.Text.Json;

namespace Cassini.Demo.SiteCatalog;

internal sealed record CatalogWrite<T>(int Revision, T Item);

// The two owner services each instantiate this store with their own domain type and file.
internal sealed class CatalogStore<T> where T : class
{
    private sealed record Receipt(string Fingerprint, CatalogWrite<T> Result);
    private sealed record State(int Revision, Dictionary<string, T> Items, Dictionary<string, Receipt> Receipts);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string? path;
    private readonly Func<T, int> version;
    private State state;

    internal CatalogStore(string? path, Func<T[]> seed, Func<T, string> key, Func<T, int> version)
    {
        this.path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        this.version = version;
        state = this.path is not null && File.Exists(this.path)
            ? JsonSerializer.Deserialize<State>(File.ReadAllBytes(this.path), Json)
                ?? throw new InvalidDataException("The site catalog is empty.")
            : new State(0, seed().ToDictionary(key, StringComparer.Ordinal), new(StringComparer.Ordinal));
    }

    internal (int Revision, T[] Items) Read()
    {
        lock (gate)
            return (state.Revision, state.Items.Values.ToArray());
    }

    internal CatalogWrite<T> Save<TCommand>(string? commandId, int? expectedVersion, string key,
        TCommand command, Func<T?, int, T> change)
    {
        if (!SiteRules.IsId(commandId) || expectedVersion is null or < 0)
            throw new SiteCatalogError(400, "InvalidCommand", "Specify a commandId and a non-negative expectedVersion.");

        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, Json)));
        lock (gate)
        {
            if (state.Receipts.TryGetValue(commandId!, out var receipt))
            {
                if (receipt.Fingerprint != fingerprint)
                    throw new SiteCatalogError(409, "CommandConflict", "This commandId was used with different inputs.");
                return receipt.Result;
            }

            var previous = state.Items.GetValueOrDefault(key);
            var currentVersion = previous is null ? 0 : version(previous);
            if (expectedVersion != currentVersion)
                throw new SiteCatalogError(409, "VersionConflict", "Read the current site before updating it.");

            var item = change(previous, checked(currentVersion + 1));
            var result = new CatalogWrite<T>(checked(state.Revision + 1), item);
            var next = new State(result.Revision, new Dictionary<string, T>(state.Items, StringComparer.Ordinal)
            {
                [key] = item,
            }, new Dictionary<string, Receipt>(state.Receipts, StringComparer.Ordinal)
            {
                [commandId!] = new Receipt(fingerprint, result),
            });

            if (path is not null)
                Persist(path, next);
            state = next;
            return result;
        }
    }

    private static void Persist(string path, State next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, next, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}

internal static class SiteSeed
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static T[] Read<T>(string path, string section)
    {
        using var seed = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!seed.RootElement.TryGetProperty(section, out var records) || records.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Missing '{section}' in the site fixture.");
        return records.Deserialize<T[]>(Json) ?? throw new InvalidDataException($"Invalid '{section}' in the site fixture.");
    }
}
