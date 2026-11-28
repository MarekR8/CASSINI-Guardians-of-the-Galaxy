namespace Cassini.Offline;

internal sealed record EwssAlert(string Level, string Text, bool Simulated);

internal interface IEwssReceiver
{
    EwssAlert Receive();
}

internal sealed class MockEwssReceiver : IEwssReceiver
{
    public EwssAlert Receive() => new("Ostrzeżenie", "Ryzyko zalania. Sprawdź zapisaną trasę ewakuacji.", true);
}
