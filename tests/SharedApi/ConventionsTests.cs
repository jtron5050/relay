using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Relay.Http;
using Xunit;

public class ConventionsTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ErrorsAreSafeAndCorrelated(string environment)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseEnvironment(environment).ConfigureServices(s => s.AddSingleton<IStartupFilter, ProbeFilter>()));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        foreach (var status in new[] { 400, 401, 403, 404, 405, 409, 410, 413, 415, 428, 429, 503 })
        {
            var correlation = Guid.NewGuid().ToString();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/probe/{status}");
            request.Headers.Add("X-Correlation-Id", correlation);
            using var response = await client.SendAsync(request);
            Assert.Equal(status, (int)response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ApiError>();
            Assert.Equal(correlation, error!.CorrelationId);
            Assert.Equal(correlation, response.Headers.GetValues("X-Correlation-Id").Single());
            Assert.Equal(status is 429 or 503, error.Retryable);
            if (error.Retryable) Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter!.Delta);
            Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync());
        }
        using var unknown = await client.GetAsync("/v1/inaccessible");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("notFound", (await unknown.Content.ReadFromJsonAsync<ApiError>())!.Code);
        using var oversized = await client.PostAsync("/probe/body", new StringContent(new string('x', 262145)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        foreach (var payload in new[] { "{", "{\"name\":\"ok\",\"unexpected\":true}" })
        {
            using var malformed = await client.PostAsync("/probe/json", new StringContent(payload, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            Assert.Equal("invalidRequest", (await malformed.Content.ReadFromJsonAsync<ApiError>())!.Code);
        }
        using var invalidCorrelation = new HttpRequestMessage(HttpMethod.Get, "/v1/missing");
        invalidCorrelation.Headers.Add("X-Correlation-Id", "bad-value");
        using var generated = await client.SendAsync(invalidCorrelation);
        Assert.True(Guid.TryParse((await generated.Content.ReadFromJsonAsync<ApiError>())!.CorrelationId, out _));
        using var openapi = await client.GetAsync("/openapi/v1.json");
        openapi.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await openapi.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/health", out _));
    }

    [Fact]
    public void ValidationBoundsAndCursorBinding()
    {
        Assert.Equal(50, ApiConventions.PageLimit(null));
        Assert.Equal(100, ApiConventions.PageLimit("100"));
        foreach (var value in new[] { "0", "101", "-1", "abc" })
            Assert.Throws<ApiFailure>(() => ApiConventions.PageLimit(value));
        Assert.Throws<ApiFailure>(() => ApiConventions.ValidateBatch(Array.Empty<int>()));
        Assert.Throws<ApiFailure>(() => ApiConventions.ValidateBatch(new int[51]));
        Assert.Throws<ApiFailure>(() => ApiConventions.RequireOpaqueId(new string('a',129)));
        var context = new DefaultHttpContext();
        Assert.Equal(428, Assert.Throws<ApiFailure>(() => ApiConventions.RequireRevision(context.Request)).Status);
        context.Request.Headers.IfMatch = "\"rev-7\"";
        Assert.Equal(7, ApiConventions.RequireRevision(context.Request));
        context.Request.Headers.IfMatch = "*";
        Assert.Throws<ApiFailure>(() => ApiConventions.RequireRevision(context.Request));
        Assert.Throws<ApiFailure>(() => ApiConventions.RequireIdempotencyKey(context.Request));
        context.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString();
        Assert.Equal(36, ApiConventions.RequireIdempotencyKey(context.Request).Length);
        var options = new JsonSerializerOptions();
        options.Converters.Add(new UtcTimestampConverter());
        Assert.EndsWith("Z\"", JsonSerializer.Serialize(DateTimeOffset.Now, options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DateTimeOffset>("\"2026-01-01T00:00:00+02:00\"", options));
        context.Request.QueryString = new QueryString("?limit=100&cursor=opaque");
        Assert.Equal((100, "opaque"), ApiConventions.Pagination(context.Request));
        context.Request.QueryString = new QueryString("?unknown=true");
        Assert.Throws<ApiFailure>(() => ApiConventions.Pagination(context.Request));
        var provider = new EphemeralDataProtectionProvider();
        var codec = new CursorCodec(provider);
        var cursor = codec.Encode("position", "actor/query");
        Assert.Equal("position", codec.Decode(cursor, "actor/query"));
        Assert.Equal(410, Assert.Throws<ApiFailure>(() => codec.Decode(cursor, "other/query")).Status);
        Assert.Throws<ApiFailure>(() => codec.Decode(cursor + "tamper", "actor/query"));
        var expired = provider.CreateProtector("Relay.v1.cursor", "actor/query").ToTimeLimitedDataProtector()
            .Protect("position", DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(410, Assert.Throws<ApiFailure>(() => codec.Decode(expired, "actor/query")).Status);
    }

    public sealed record ProbeRequest(string Name);

    private sealed class ProbeFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Path.StartsWithSegments("/probe", out var remaining) &&
                    int.TryParse(remaining.Value?.Trim('/'), out var status))
                {
                    if (status == 503) throw new InvalidOperationException("secret credential https://private");
                    context.Response.StatusCode = status;
                    return;
                }
                if (context.Request.Path == "/probe/json")
                {
                    await context.Request.ReadFromJsonAsync<ProbeRequest>();
                    context.Response.StatusCode = 204;
                    return;
                }
                await continuation(context);
            });
        };
    }
}
