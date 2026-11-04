using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Relay.AdminApi.Authentication;

public sealed class StaffAuthenticationOptions
{
    public string Issuer { get; set; } = "";
    public string ClientId { get; set; } = "";
}

public static class StaffAuthentication
{
    public static IServiceCollection AddStaffAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StaffAuthenticationOptions>().Bind(configuration.GetSection("StaffAuthentication"))
            .Validate(o => Uri.TryCreate(o.Issuer, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment),
                "StaffAuthentication:Issuer must be an absolute HTTPS issuer URL.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ClientId), "StaffAuthentication:ClientId is required.")
            .ValidateOnStart();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<StaffAuthenticationOptions>>((options, configured) =>
            {
                var staff = configured.Value;
                options.Authority = staff.Issuer;
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = staff.Issuer,
                    // Cognito SRP access tokens bind the client through client_id, not aud.
                    ValidateAudience = false,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    RequireExpirationTime = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal!;
                        if (!string.Equals(context.SecurityToken.Issuer, staff.Issuer, StringComparison.Ordinal) ||
                            principal.FindAll("token_use").Select(c => c.Value).ToArray() is not ["access"] ||
                            principal.FindAll("client_id").Select(c => c.Value).ToArray() is not [var client] ||
                            !string.Equals(client, staff.ClientId, StringComparison.Ordinal) ||
                            principal.FindAll("sub").Select(c => c.Value).ToArray() is not [var subject] ||
                            string.IsNullOrWhiteSpace(subject))
                            context.Fail("Invalid staff access token.");
                        return Task.CompletedTask;
                    }
                };
            });
        services.AddAuthorization(options =>
        {
            var deny = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireAssertion(_ => false).Build();
            options.DefaultPolicy = deny;
            options.FallbackPolicy = deny;
            foreach (var permission in StaffPermissions.All)
                options.AddPolicy(permission, policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser().RequireAssertion(context => context.User.FindAll("cognito:groups")
                        .Any(group => StaffPermissions.Grants(group.Value, permission))));
        });
        return services;
    }
}

public static class StaffPermissions
{
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
    {
        "fleet.read", "catalog.read", "deployments.read", "history.read", "audit.read",
        "customers.write", "applications.write", "enrollment.write", "computers.revoke", "credentials.rotate",
        "releases.write", "releases.publish", "releases.withdraw", "channels.current.write",
        "assignments.write", "pins.write", "deployments.write", "rollback.authorize"
    });

    public static bool Grants(string group, string permission) => All.Contains(permission) && group switch
    {
        "Administrator" => true,
        "ReleaseOperator" => permission is not ("customers.write" or "applications.write" or "enrollment.write"
            or "computers.revoke" or "credentials.rotate"),
        "SupportViewer" => permission is "fleet.read" or "catalog.read" or "deployments.read" or "history.read" or "audit.read",
        _ => false
    };
}
