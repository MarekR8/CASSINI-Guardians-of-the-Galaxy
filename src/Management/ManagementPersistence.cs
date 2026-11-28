using System.Text.Json;

namespace Cassini.Management;

internal interface IManagementPersistence
{
    ManagementState Read();

    /// <summary>
    /// Runs <paramref name="apply"/> under the store's write lock and persists a non-null next state in one commit.
    /// <paramref name="committed"/> runs after a successful commit.
    /// </summary>
    IResult Update(Func<ManagementState, (ManagementState? Next, IResult Result)> apply,
        Action<ManagementState>? committed = null);
}

internal sealed class ManagementPersistenceException(Exception inner)
    : Exception("The Management state is unavailable.", inner);

/// <summary>Default offline persistence: one JSON file replaced atomically, or memory when no directory is set.</summary>
internal sealed class FileManagementPersistence : IManagementPersistence
{
    private readonly object gate = new();
    private readonly string? path;
    private ManagementState state;

    public FileManagementPersistence(string? dataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
            path = Path.Combine(dataDirectory, "management.json");
        }

        state = path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<ManagementState>(File.ReadAllBytes(path), ManagementStore.Json)
                ?? throw new InvalidDataException("Invalid Management demo state.")
            : ManagementState.Initial();
    }

    public ManagementState Read()
    {
        lock (gate)
            return state;
    }

    public IResult Update(Func<ManagementState, (ManagementState? Next, IResult Result)> apply,
        Action<ManagementState>? committed = null)
    {
        lock (gate)
        {
            var (next, result) = apply(state);
            if (next is null)
                return result;
            try
            {
                if (path is not null)
                {
                    var temporaryPath = path + ".tmp";
                    File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(next, ManagementStore.Json));
                    File.Move(temporaryPath, path, overwrite: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ManagementPersistenceException(exception);
            }

            state = next;
            committed?.Invoke(next);
            return result;
        }
    }
}
