using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cassini.Management.Grouping;

internal sealed class GroupingAccess
{
    private sealed record Account(string OperatorId, string Role, string IncidentId, string TokenSha256);

    private readonly Dictionary<string, Account>? accounts;

    internal GroupingAccess(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var entries = document.RootElement.GetProperty("operators").Deserialize<Account[]>(ManagementStore.Json);
        if (entries is not { Length: > 0 })
            throw new InvalidDataException("Configure at least one Grouping operator.");

        accounts = new(StringComparer.OrdinalIgnoreCase);
        foreach (var account in entries)
        {
            if (account is null || string.IsNullOrWhiteSpace(account.OperatorId) || string.IsNullOrWhiteSpace(account.IncidentId) ||
                account.Role is not ("coordinator" or "field-operator" or "reader") ||
                account.TokenSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit) ||
                !accounts.TryAdd(hash, account))
                throw new InvalidDataException("Invalid or duplicate Grouping operator credentials.");
        }
    }

    internal ValueTask<object?> CheckAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (accounts is null)
            return next(context); // Explicitly enabled exercise evidence uses synthetic roles.

        var request = context.HttpContext.Request;
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header) ||
            !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            header.Parameter is not { Length: > 0 } token ||
            !accounts.TryGetValue(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), out var actor))
        {
            context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
            return ValueTask.FromResult<object?>(ManagementStore.Error("AuthenticationRequired", "Provide a Grouping bearer token.", 401));
        }

        var command = context.Arguments.OfType<IGroupingCommand>().SingleOrDefault();
        var incidentId = command is null ? request.Query["incidentId"].ToString() : command.IncidentId;
        if (incidentId != actor.IncidentId)
            return Forbidden("IncidentNotAssigned", "The account is not assigned to this incident.");
        if (command is not null && actor.Role != "coordinator" &&
            !(command is RecordCareHandover && actor.Role == "field-operator"))
            return Forbidden("RoleNotPermitted", "The account cannot perform this Grouping command.");

        if (command is RecordCareHandover { Handover: { } handover } transfer)
            context.Arguments[context.Arguments.IndexOf(command)] = transfer with
            {
                Handover = handover with { AuthorizedBy = actor.OperatorId, Role = actor.Role }
            };
        return next(context);
    }

    private static ValueTask<object?> Forbidden(string code, string detail) =>
        ValueTask.FromResult<object?>(ManagementStore.Error(code, detail, 403));
}
