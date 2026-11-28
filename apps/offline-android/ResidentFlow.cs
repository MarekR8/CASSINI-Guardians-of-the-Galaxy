namespace Cassini.Offline;

internal enum ResidentAction
{
    FindRoute,
    ConfirmExit,
    ExitSaved,
    RouteUnavailable,
    ReturnToRoute,
}

internal static class ResidentFlow
{
    internal static ResidentAction NextAction(bool showingArchive, bool hasRoute, bool hasOptions, bool exitSaved) =>
        (showingArchive, hasRoute, hasOptions, exitSaved) switch
        {
            (true, _, _, _) => ResidentAction.ReturnToRoute,
            (_, _, false, _) => ResidentAction.RouteUnavailable,
            (_, true, _, true) => ResidentAction.ExitSaved,
            (_, true, _, false) => ResidentAction.ConfirmExit,
            _ => ResidentAction.FindRoute,
        };
}
