using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;

// Optional Keycloak sign-in (compose profile `full`). Without `Auth:Enabled=true` nothing is registered:
// no authentication, unchanged CSP, and /auth-config.json reports {enabled:false}.
sealed record GatewayAuthSettings(bool Enabled, string? Url, string Realm, string ClientId, string Audience,
    string? MetadataAddress, bool RequireHttpsMetadata)
{
    public string? Issuer => Url is null ? null : $"{Url.TrimEnd('/')}/realms/{Realm}";

    // Extra connect-src origin for the browser token endpoint (Keycloak), empty when disabled.
    public string ConnectSrc => Enabled && Uri.TryCreate(Url, UriKind.Absolute, out var url)
        ? "'self' " + url.GetLeftPart(UriPartial.Authority)
        : "'self'";
}

static class GatewayAuth
{
    public const string Coordinator = "coordinator";
    public const string Dispatcher = "dispatcher";
    public const string Responder = "responder";
    public const string Resident = "resident";

    public static GatewayAuthSettings AddGatewayAuth(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("Auth");
        var settings = new GatewayAuthSettings(
            section.GetValue("Enabled", false),
            NullIfEmpty(section["Url"]),
            NullIfEmpty(section["Realm"]) ?? "cassini",
            NullIfEmpty(section["ClientId"]) ?? "cassini-dashboard",
            NullIfEmpty(section["Audience"]) ?? "cassini-gateway",
            NullIfEmpty(section["MetadataAddress"]),
            section.GetValue("RequireHttpsMetadata", true));
        if (!settings.Enabled)
            return settings;
        if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out _))
            throw new InvalidOperationException("Auth:Enabled=true requires an absolute Auth:Url (browser-facing Keycloak URL).");

        builder.Services.AddTransient<IClaimsTransformation, RealmRoleClaims>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = settings.Issuer;
            if (settings.MetadataAddress is not null)
                options.MetadataAddress = settings.MetadataAddress;
            options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
            options.Audience = settings.Audience;
            options.MapInboundClaims = false;
            options.TokenValidationParameters.ValidIssuer = settings.Issuer;
            options.TokenValidationParameters.ValidAudience = settings.Audience;
            options.TokenValidationParameters.NameClaimType = "preferred_username";
            options.TokenValidationParameters.RoleClaimType = "role";
        });
        return settings;
    }

    public static void UseGatewayAuth(this WebApplication app, GatewayAuthSettings settings)
    {
        app.MapGet("/auth-config.json", () => settings.Enabled
            ? new { enabled = true, url = settings.Url, realm = (string?)settings.Realm, clientId = (string?)settings.ClientId }
            : new { enabled = false, url = (string?)null, realm = (string?)null, clientId = (string?)null });
        if (!settings.Enabled)
            return;

        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api", out var rest))
            {
                await next();
                return;
            }
            var user = context.User;
            if (user.Identity?.IsAuthenticated != true)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await Reject(context, StatusCodes.Status401Unauthorized, "Unauthorized");
                return;
            }
            var read = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
            if (!(read ? CanRead(user, rest.Value ?? "") : CanCommand(user, rest.Value ?? "")))
            {
                await Reject(context, StatusCodes.Status403Forbidden, "Forbidden");
                return;
            }
            await next();
        });
    }

    // Writes go only to /api/{service}/commands/{operation}; anything else that is not a read is coordinator-only.
    // The link simulator's link-profile and Management's ewss-alert match no role rule, so only the coordinator
    // sends them. Field-relayed commands (X-Cassini-Link: field) use the same path and are checked before the relay.
    static bool CanCommand(ClaimsPrincipal user, string path)
    {
        if (user.IsInRole(Coordinator))
            return true;
        if (path.Trim('/').Split('/') is not [var service, var group, var operation] ||
            !Same(group, "commands"))
            return false;
        if (user.IsInRole(Dispatcher) && Same(service, "dispatch"))
            return true;
        if (user.IsInRole(Responder) && Same(operation, "progress") && (Same(service, "dispatch") || Same(service, "management")))
            return true;
        // Residents only draw their evacuation route at the node. Confirming their own progress needs a caseId claim
        // that binds the user to one case; the realm does not issue it yet, so a resident cannot change any case (INV-12).
        return user.IsInRole(Resident) && Same(service, "routing") && Same(operation, "draw");
    }

    // Staff roles retain their existing access. Residents may read only explicitly public network and
    // situation operations. New owner queries are not automatically public; case-bound access needs a case claim.
    internal static bool CanRead(ClaimsPrincipal user, string path)
    {
        if (user.IsInRole(Coordinator) || user.IsInRole(Dispatcher) || user.IsInRole(Responder))
            return true;
        if (!user.IsInRole(Resident))
            return false;
        var segments = path.Trim('/').Split('/');
        if (segments.Length is < 3 or > 4 || !Same(segments[1], "queries"))
            return false;
        var service = segments[0];
        var operation = segments[2];
        if (Same(service, "situation"))
            return segments.Length == 3 && (Same(operation, "snapshot") || Same(operation, "closed-roads"));
        if (!Same(service, "routing"))
            return false;
        return segments.Length == 3
            ? Same(operation, "network") || Same(operation, "distribution") || Same(operation, "city-route") ||
              Same(operation, "validate-route")
            : segments[3].Length > 0 && (Same(operation, "draws") || Same(operation, "city-node") || Same(operation, "city-edge"));
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static Task Reject(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { code });
    }

    static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // Keycloak puts realm roles in realm_access.roles; expose them as role claims for IsInRole.
    sealed class RealmRoleClaims : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity ||
                identity.HasClaim(claim => claim.Type == identity.RoleClaimType))
                return Task.FromResult(principal);
            var realmAccess = identity.FindFirst("realm_access")?.Value;
            if (realmAccess is null)
                return Task.FromResult(principal);
            try
            {
                using var document = JsonDocument.Parse(realmAccess);
                if (document.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
                    foreach (var role in roles.EnumerateArray())
                        if (role.GetString() is { Length: > 0 } name)
                            identity.AddClaim(new Claim(identity.RoleClaimType, name));
            }
            catch (JsonException)
            {
            }
            return Task.FromResult(principal);
        }
    }
}
