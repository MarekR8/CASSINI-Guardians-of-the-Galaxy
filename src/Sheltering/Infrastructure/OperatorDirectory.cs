using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cassini.Demo.SiteCatalog;

namespace Cassini.Sheltering.Infrastructure;

/// <summary>An authenticated caller and the incident and sites it may act on.</summary>
internal sealed record OperatorIdentity(string OperatorId, string Role, string IncidentId, string[] ShelterIds);

/// <summary>
/// Stage B role boundary for reception. Accounts come from <c>Sheltering:OperatorsFile</c>, which holds only SHA-256
/// hashes of bearer tokens and is generated outside Git for each exercise. Authority comes from the token, never from
/// the request body. Without the file, the Stage A exercise runs unauthenticated with synthetic roles.
/// </summary>
internal sealed class OperatorDirectory
{
    internal const string ShelterOperator = "shelter-operator";
    internal const string Coordinator = "coordinator";

    private sealed record Account(string? OperatorId, string? Role, string? IncidentId, string[]? ShelterIds,
        string? TokenSha256);

    private readonly Dictionary<string, OperatorIdentity>? byHash;

    internal OperatorDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var accounts = document.RootElement.GetProperty("operators")
            .Deserialize<Account[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        byHash = new(StringComparer.Ordinal);
        foreach (var account in accounts)
        {
            if (!SiteRules.IsId(account.OperatorId) || account.Role is not (ShelterOperator or Coordinator) ||
                !SiteRules.IsId(account.IncidentId) || account.TokenSha256 is not { Length: 64 } hash ||
                (account.ShelterIds ?? []).Any(id => !SiteRules.IsId(id)) ||
                (account.Role == ShelterOperator && account.ShelterIds is not { Length: > 0 }))
                throw new InvalidDataException($"Invalid operator account in '{path}'.");
            byHash[hash.ToLowerInvariant()] = new(account.OperatorId!, account.Role!, account.IncidentId!,
                account.ShelterIds ?? []);
        }
    }

    internal bool Enabled => byHash is not null;

    /// <summary>The caller, or null when authentication is not configured.</summary>
    internal OperatorIdentity? Authenticate(HttpContext context)
    {
        if (byHash is null)
            return null;
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length <= 7)
            throw Unauthenticated();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(header[7..].Trim()))).ToLowerInvariant();
        return byHash.TryGetValue(hash, out var identity) ? identity : throw Unauthenticated();
    }

    /// <summary>Only a shelter operator assigned to the site and incident reports admissions and departures.</summary>
    internal static void RequireReporter(OperatorIdentity? caller, string? incidentId, string? shelterId)
    {
        if (caller is null)
            return;
        if (caller.Role != ShelterOperator)
            throw new SiteCatalogError(403, "RoleNotPermitted", "Only a shelter operator reports admissions and departures.");
        RequireScope(caller, incidentId, shelterId);
    }

    /// <summary>Readers see their incident; shelter operators only their assigned sites.</summary>
    internal static void RequireScope(OperatorIdentity? caller, string? incidentId, string? shelterId)
    {
        if (caller is null)
            return;
        if (incidentId is not null && incidentId != caller.IncidentId)
            throw new SiteCatalogError(403, "IncidentNotAssigned", "The account is not assigned to this incident.");
        if (caller.Role == ShelterOperator && shelterId is not null && !caller.ShelterIds.Contains(shelterId))
            throw new SiteCatalogError(403, "ShelterNotAssigned", "The account is not assigned to this shelter.");
    }

    internal static bool CanSee(OperatorIdentity? caller, string shelterId) =>
        caller is null || caller.Role == Coordinator || caller.ShelterIds.Contains(shelterId);

    private static SiteCatalogError Unauthenticated() =>
        new(401, "AuthenticationRequired", "Sign in with an exercise operator token.");
}
