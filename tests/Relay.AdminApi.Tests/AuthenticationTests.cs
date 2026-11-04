using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Relay.AdminApi.Authentication;
using Relay.Http;
using Xunit;

namespace Relay.AdminApi.Tests;

public class AuthenticationTests
{
    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test-key" };

    private static ApiTestFactory Factory() => new AuthFactory();

    private sealed class AuthFactory(RotatingConfiguration? rotation = null) : ApiTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Configuration = new OpenIdConnectConfiguration { Issuer = "https://staff.example.test/pool" };
                    options.Configuration.SigningKeys.Add(Key);
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
                    if (rotation is not null)
                    {
                        options.Configuration = null;
                        options.ConfigurationManager = rotation;
                    }
                });
                services.AddSingleton<IStartupFilter, Routes>();
            });
        }
    }

    private sealed class Routes : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseRouting();
            next(app);
            app.UseEndpoints(endpoints =>
            {
                foreach (var permission in StaffPermissions.All)
                    endpoints.MapGet("/v1/probe/" + permission, () => Results.NoContent()).RequireAuthorization(permission);
                endpoints.MapGet("/v1/unannotated", () => Results.NoContent());
                endpoints.MapGet("/v1/default", () => Results.NoContent()).RequireAuthorization();
            });
        };
    }

    private static string Token(string[]? groups = null, string variant = "valid")
    {
        var claims = new List<Claim> { new("token_use", variant == "id" ? "id" : "access"), new("client_id", variant == "client" ? "wrong" : "test-client"), new("sub", "staff-subject") };
        if (variant.StartsWith("missing-")) claims.RemoveAll(c => c.Type == variant[8..]);
        foreach (var group in groups ?? []) claims.Add(new("cognito:groups", group));
        claims.Add(new("permission", "credentials.rotate"));
        var now = DateTime.UtcNow;
        var key = variant == "signature" ? new RsaSecurityKey(RSA.Create(2048)) : Key;
        var token = new JwtSecurityToken(variant == "issuer" ? "https://wrong.test" : "https://staff.example.test/pool",
            claims: claims, notBefore: now.AddMinutes(variant == "future" ? 5 : -10),
            expires: now.AddMinutes(variant == "expired" ? -5 : 10),
            signingCredentials: variant == "unsigned" ? null : new SigningCredentials(key,
                variant == "algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256));
        if (variant == "missing-exp") token.Payload.Remove("exp");
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static IEnumerable<object[]> Permissions()
    {
        var reads = new[] { "fleet.read", "catalog.read", "deployments.read", "history.read", "audit.read" };
        var adminOnly = new[] { "customers.write", "applications.write", "enrollment.write", "computers.revoke", "credentials.rotate" };
        foreach (var permission in StaffPermissions.All)
            foreach (var group in new[] { "Administrator", "ReleaseOperator", "SupportViewer", "administrator", "Unknown", "" })
                yield return [permission, group, group == "Administrator" || group == "ReleaseOperator" && !adminOnly.Contains(permission) || group == "SupportViewer" && reads.Contains(permission)];
    }

    [Theory]
    [MemberData(nameof(Permissions))]
    public async Task PermissionMatrix(string permission, string group, bool allowed)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(group.Length == 0 ? [] : [group]));
        using var response = await client.GetAsync("/v1/probe/" + permission);
        Assert.Equal(allowed ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("unsigned")][InlineData("signature")][InlineData("expired")]
    [InlineData("future")][InlineData("algorithm")][InlineData("issuer")][InlineData("client")][InlineData("id")]
    [InlineData("missing-token_use")][InlineData("missing-client_id")][InlineData("missing-sub")][InlineData("missing-exp")]
    public async Task InvalidTokensAreSafe(string variant)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var token = variant == "malformed" ? "secret-token" : Token(["Administrator"], variant);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var correlation = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", correlation);
        using var response = await client.GetAsync("/v1/probe/fleet.read");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().ToString());
        Assert.Equal(correlation, (await response.Content.ReadFromJsonAsync<ApiError>())!.CorrelationId);
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DefaultsDenyAndGroupsAreAdditive()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        foreach (var path in new[] { "/v1/unannotated", "/v1/default", "/v1/missing", "/v1/probe/fleet.read" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(["Administrator"]));
        foreach (var path in new[] { "/v1/unannotated", "/v1/default", "/v1/missing" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(["Unknown", "SupportViewer", "ReleaseOperator"]));
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/v1/probe/releases.publish")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/probe/credentials.rotate")).StatusCode);
        foreach (var path in new[] { "/health", "/openapi/v1.json" })
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid");
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        }
    }

    private sealed class RotatingConfiguration : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public bool Refreshed { get; private set; }
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = "https://staff.example.test/pool" };
            configuration.SigningKeys.Add(Refreshed ? Key : new RsaSecurityKey(RSA.Create(2048)) { KeyId = "old-key" });
            return Task.FromResult(configuration);
        }
        public void RequestRefresh() => Refreshed = true;
    }

    [Fact]
    public async Task UnknownKeyRequestsRefreshAndRemainsDeniedUntilTrusted()
    {
        var configuration = new RotatingConfiguration();
        await using var factory = new AuthFactory(configuration);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(["Administrator"]));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/probe/fleet.read")).StatusCode);
        Assert.True(configuration.Refreshed);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/v1/probe/fleet.read")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(["Administrator"], "signature"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/probe/fleet.read")).StatusCode);
    }

    [Theory]
    [InlineData("", "client")][InlineData("http://staff.test/pool", "client")][InlineData("https://staff.test/pool", "")]
    public void InvalidConfigurationFailsStartup(string issuer, string clientId)
    {
        using var factory = new ApiTestFactory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StaffAuthentication:Issuer"] = issuer, ["StaffAuthentication:ClientId"] = clientId
            })));
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }
}
