using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;

namespace Relay.Http;

public sealed record ApiError(string Code, string Message, string CorrelationId, bool Retryable);

public sealed class ApiFailure(int status, string code, string safeMessage, bool retryable = false) : Exception(safeMessage)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public static class ApiConventions
{
    public const int MaxBodyBytes = 256 * 1024;

    public static void AddRelayHttp(this WebApplicationBuilder builder)
    {
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new UtcTimestampConverter());
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        });
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxBodyBytes);
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<CursorCodec>();
    }

    public static void UseRelayHttp(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var supplied = context.Request.Headers["X-Correlation-Id"].ToString();
            context.TraceIdentifier = Guid.TryParseExact(supplied, "D", out var id)
                ? id.ToString("D") : Guid.NewGuid().ToString("D");
            context.Response.Headers["X-Correlation-Id"] = context.TraceIdentifier;
            context.Response.Headers.CacheControl = "no-store";
            var originalBody = context.Request.Body;
            using var bufferedBody = new MemoryStream();
            try
            {
                var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = MaxBodyBytes;
                if (context.Request.ContentLength > MaxBodyBytes)
                    throw new ApiFailure(413, "requestTooLarge", "The request body exceeds the limit.");
                var chunk = new byte[8192];
                int read;
                while ((read = await originalBody.ReadAsync(chunk, context.RequestAborted)) > 0)
                {
                    if (bufferedBody.Length + read > MaxBodyBytes)
                        throw new ApiFailure(413, "requestTooLarge", "The request body exceeds the limit.");
                    bufferedBody.Write(chunk, 0, read);
                }
                bufferedBody.Position = 0;
                context.Request.Body = bufferedBody;
                await next(context);
                if (context.Response.StatusCode >= 400 && !context.Response.HasStarted)
                    await WriteError(context, context.Response.StatusCode);
            }
            catch (Exception exception) when (!context.Response.HasStarted)
            {
                if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested) return;
                var status = exception switch
                {
                    ApiFailure failure => failure.Status,
                    BadHttpRequestException bad => bad.StatusCode,
                    JsonException => 400,
                    _ => 503
                };
                // Never log exception text: dependency messages can contain credentials or signed URLs.
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Relay.Http")
                    .LogWarning("Request failed with status {Status}; correlation {CorrelationId}", status, context.TraceIdentifier);
                await WriteError(context, status, exception as ApiFailure);
            }
            finally { context.Request.Body = originalBody; }
        });
    }

    private static async Task WriteError(HttpContext context, int status, ApiFailure? failure = null)
    {
        var (code, message) = status switch
        {
            400 => ("invalidRequest", "The request is invalid."),
            401 => ("unauthorized", "Valid credentials are required."),
            403 => ("forbidden", "Permission is required for this operation."),
            404 => ("notFound", "The resource was not found."),
            405 => ("methodNotAllowed", "The method is not supported."),
            409 => ("conflict", "The request conflicts with current state."),
            410 => ("expired", "The reference has expired."),
            413 => ("requestTooLarge", "The request body exceeds the limit."),
            415 => ("unsupportedMediaType", "Use application/json."),
            428 => ("revisionRequired", "A revision precondition is required."),
            429 => ("throttled", "Retry the request later."),
            _ => ("serviceUnavailable", "The service is temporarily unavailable.")
        };
        var retryable = failure?.Retryable == true || status is 429 or 503;
        context.Response.StatusCode = status;
        if (retryable) context.Response.Headers.RetryAfter = "5";
        await context.Response.WriteAsJsonAsync(new ApiError(failure?.Code ?? code,
            failure?.Message ?? message, context.TraceIdentifier, retryable));
    }

    public static string RequireOpaqueId(string value)
    {
        if (value.Length is < 1 or > 128 || value.Any(c => c < 33 || c > 126))
            throw new ApiFailure(400, "invalidId", "The identifier is invalid.");
        return value;
    }

    public static long RequireRevision(HttpRequest request)
    {
        var value = request.Headers.IfMatch.ToString();
        if (value.Length == 0) throw new ApiFailure(428, "revisionRequired", "If-Match is required.");
        if (!value.StartsWith("\"rev-", StringComparison.Ordinal) || !value.EndsWith('"') ||
            !long.TryParse(value.AsSpan(5, value.Length - 6), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 1)
            throw new ApiFailure(400, "invalidRevision", "If-Match must contain one revision ETag.");
        return revision;
    }

    public static string RequireIdempotencyKey(HttpRequest request)
    {
        var value = request.Headers["Idempotency-Key"].ToString();
        if (value.Length is < 16 or > 128 || value.Any(c => c < 32 || c > 126))
            throw new ApiFailure(400, "invalidIdempotencyKey", "A valid Idempotency-Key is required.");
        return value;
    }

    public static (int Limit, string? Cursor) Pagination(HttpRequest request, params string[] allowedFilters)
    {
        foreach (var pair in request.Query)
            if ((pair.Key != "limit" && pair.Key != "cursor" && !allowedFilters.Contains(pair.Key)) || pair.Value.Count != 1)
                throw new ApiFailure(400, "invalidQuery", "The query is invalid.");
        var cursor = request.Query["cursor"].FirstOrDefault();
        if (cursor is not null && cursor.Length is < 1 or > 4096)
            throw new ApiFailure(400, "invalidCursor", "The cursor is invalid.");
        return (PageLimit(request.Query["limit"].FirstOrDefault()), cursor);
    }

    public static int PageLimit(string? value) => value is null ? 50 :
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit is >= 1 and <= 100
            ? limit : throw new ApiFailure(400, "invalidLimit", "Limit must be between 1 and 100.");

    public static void ValidateBatch<T>(IReadOnlyCollection<T> items, int maximum = 50)
    {
        if (items.Count < 1 || items.Count > maximum)
            throw new ApiFailure(400, "invalidBatch", "The batch size is invalid.");
    }
}

public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed class CursorCodec(IDataProtectionProvider provider)
{
    public string Encode(string position, string scope)
    {
        var cursor = provider.CreateProtector("Relay.v1.cursor", scope).ToTimeLimitedDataProtector()
            .Protect(position, TimeSpan.FromMinutes(15));
        if (cursor.Length > 4096)
            throw new ApiFailure(503, "cursorUnavailable", "The page cannot be continued safely.", true);
        return cursor;
    }

    public string Decode(string cursor, string scope)
    {
        if (cursor.Length is < 1 or > 4096)
            throw new ApiFailure(400, "invalidCursor", "The cursor is invalid.");
        try { return provider.CreateProtector("Relay.v1.cursor", scope).ToTimeLimitedDataProtector().Unprotect(cursor); }
        catch (System.Security.Cryptography.CryptographicException)
        { throw new ApiFailure(410, "cursorExpired", "The cursor is invalid or expired."); }
    }
}

public sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || reader.GetString() is not { } value ||
            !value.EndsWith('Z') || !reader.TryGetDateTimeOffset(out var timestamp))
            throw new JsonException("A UTC RFC 3339 timestamp is required.");
        return timestamp;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
}
