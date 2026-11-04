using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Relay.Core.Audit;
using Xunit;

namespace Relay.AdminApi.Tests;

public class AuditEndpointTests
{
    private sealed class History : IAuditHistory
    {
        public AuditQuery? LastQuery { get; private set; }
        public Task<AuditPage> ListAsync(AuditQuery query, string? cursor = null, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(new AuditPage([], null));
        }
    }

    [Theory]
    [InlineData("Administrator", 200)][InlineData("ReleaseOperator", 200)]
    [InlineData("SupportViewer", 200)][InlineData("Unknown", 403)][InlineData(null, 401)]
    public async Task RequiresAuditPermission(string? group, int expected)
    {
        var history = new History();
        await using var factory = new AuthenticationTests.AuthFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IAuditHistory>(history)));
        using var client = factory.CreateClient();
        if (group is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", AuthenticationTests.Token([group]));
        using var response = await client.GetAsync("/v1/audit-events?from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z");
        Assert.Equal(expected, (int)response.StatusCode);
        if (expected == 200)
        {
            Assert.Equal("staff-subject", history.LastQuery!.ActorId);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("nextCursor").ValueKind);
        }
        else Assert.Null(history.LastQuery);
    }

    [Theory]
    [InlineData("from=2026-01-01Z&to=2026-01-02Z")]
    [InlineData("from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z&limit=1&limit=2")]
    [InlineData("")][InlineData("from=2026-01-01T00:00:00Z")]
    [InlineData("from=2026-01-01T00:00:00Z&to=2026-03-01T00:00:00Z")]
    [InlineData("from=2026-01-02T00:00:00Z&to=2026-01-01T00:00:00Z")]
    [InlineData("from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z&targetType=test")]
    [InlineData("from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z&actor=staff")]
    [InlineData("from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z&limit=101")]
    public async Task RejectsInvalidQueries(string query)
    {
        var history = new History();
        await using var factory = new AuthenticationTests.AuthFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IAuditHistory>(history)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", AuthenticationTests.Token(["SupportViewer"]));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/audit-events?" + query)).StatusCode);
        Assert.Null(history.LastQuery);
    }
}
