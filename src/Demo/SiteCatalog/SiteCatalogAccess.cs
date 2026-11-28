using System.Security.Claims;
#if SITE_CATALOG_STAGE_B
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
#endif

namespace Cassini.Demo.SiteCatalog;

internal sealed class SiteCatalogAccess(string mode)
{
    internal static bool Configure(WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var mode = config["SiteCatalog:AccessMode"] ?? "readonly";
        if (mode is not ("readonly" or "demo" or "oidc"))
            throw new InvalidOperationException("Use readonly, demo or oidc for SiteCatalog:AccessMode.");
        builder.Services.AddSingleton(new SiteCatalogAccess(mode));
        if (mode != "oidc")
            return false;

#if SITE_CATALOG_STAGE_B
        var audience = config["SiteCatalog:Audience"];
        if (string.IsNullOrWhiteSpace(audience) ||
            !Uri.TryCreate(config["SiteCatalog:Authority"], UriKind.Absolute, out var authority) ||
            string.IsNullOrEmpty(authority.Host) || authority.UserInfo.Length > 0 ||
            authority.Query.Length > 0 || authority.Fragment.Length > 0 ||
            (authority.Scheme != "https" && !(authority.Scheme == "http" && authority.IsLoopback &&
                config.GetValue<bool>("SiteCatalog:AllowInsecureLoopbackMetadata"))))
            throw new InvalidOperationException("OIDC requires a trusted HTTPS authority and an API audience.");

        var issuer = authority.AbsoluteUri.TrimEnd('/');
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = issuer;
            options.Audience = audience;
            options.RequireHttpsMetadata = authority.Scheme == "https";
            options.MapInboundClaims = false;
            options.TokenValidationParameters.ValidateIssuerSigningKey = true;
            options.TokenValidationParameters.ValidIssuer = issuer;
            options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
            options.IncludeErrorDetails = false;
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    var user = context.Principal;
                    if (user?.FindFirstValue("iss") != issuer ||
                        (user.FindFirstValue("typ") != "Bearer" &&
                         context.SecurityToken is not JsonWebToken { Typ: "at+jwt" or "application/at+jwt" }))
                        context.Fail("The catalog requires an access token from its configured issuer.");
                    return Task.CompletedTask;
                },
            };
        });
        return true;
#else
        throw new InvalidOperationException("Build with EnableSiteCatalogStageB=true to use OIDC.");
#endif
    }

    internal CatalogActor GetActor(HttpContext context, string? incidentId, string? shelterId = null)
    {
        if (mode == "demo")
            return CatalogActor.Demo;
        if (mode == "readonly")
            throw new SiteCatalogError(403, "CatalogReadOnly", "Site catalog writes are disabled.");

        var user = context.User;
        if (user.Identity?.IsAuthenticated != true ||
            user.FindFirstValue("sub") is not { Length: > 0 and <= 256 } subject ||
            user.FindFirstValue("iss") is not { Length: > 0 and <= 1024 } issuer)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            throw new SiteCatalogError(401, "AuthenticationRequired", "A valid access token is required.");
        }

        if (SiteRules.IsId(incidentId) && user.HasClaim("incident_scope", incidentId!))
        {
            if (user.HasClaim("site_role", "coordinator"))
                return new(issuer, subject, "coordinator");
            if (SiteRules.IsId(shelterId) && user.HasClaim("site_role", "shelter-operator") &&
                user.HasClaim("shelter_scope", shelterId!))
                return new(issuer, subject, "shelter-operator");
        }

        throw Forbidden();
    }

    internal static SiteCatalogError Forbidden() =>
        new(403, "SiteScopeForbidden", "This role or scope does not allow the requested site change.");
}
