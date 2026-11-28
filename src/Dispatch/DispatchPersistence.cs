using System.Text.Json;

namespace Cassini.Dispatch;

internal interface IDispatchPersistence
{
    DispatchState Read();
    IResult Update(Func<DispatchState, (DispatchState? Next, IResult Result)> apply);
}

internal sealed class DispatchPersistenceException(Exception inner)
    : Exception("The Dispatch state is unavailable.", inner);

internal sealed class FileDispatchPersistence(string? dataDirectory) : IDispatchPersistence
{
    private readonly object gate = new();
    private readonly string? path = DispatchStore.StatePath(dataDirectory);
    private DispatchState state = DispatchStore.InitialState(dataDirectory);

    public DispatchState Read()
    {
        lock (gate)
            return state;
    }

    public IResult Update(Func<DispatchState, (DispatchState? Next, IResult Result)> apply)
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
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    var tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(next, DispatchStore.Json));
                    File.Move(tmp, path, overwrite: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new DispatchPersistenceException(exception);
            }

            state = next;
            return result;
        }
    }
}
