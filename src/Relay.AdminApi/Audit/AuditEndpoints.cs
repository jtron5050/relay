using System.Globalization;
using Relay.Core.Audit;
using Relay.Core.Persistence;
using Relay.Http;

namespace Relay.AdminApi.Audit;

public static class AuditEndpoints
{
    public static void MapAudit(this WebApplication app) => app.MapGet("/v1/audit-events", async
        (HttpContext context, IAuditHistory history, CancellationToken cancellationToken) =>
        {
            var (limit, cursor) = ApiConventions.Pagination(context.Request, "from", "to", "targetType", "targetId");
            var query = context.Request.Query;
            DateTimeOffset Timestamp(string name)
            {
                var value = query[name].ToString();
                if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$") || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var timestamp))
                    throw new ApiFailure(400, "invalidQuery", "UTC from and to timestamps are required.");
                return timestamp;
            }
            var from = Timestamp("from");
            var to = Timestamp("to");
            var type = query["targetType"].FirstOrDefault();
            var id = query["targetId"].FirstOrDefault();
            if (from >= to || to - from > TimeSpan.FromDays(31) || (type is null) != (id is null))
                throw new ApiFailure(400, "invalidQuery", "Use a nonempty range of at most 31 days and paired target filters.");
            if (type is not null) { ApiConventions.RequireOpaqueId(type); ApiConventions.RequireOpaqueId(id!); }
            try
            {
                var page = await history.ListAsync(new(context.User.FindFirst("sub")!.Value, from, to, limit, type, id), cursor, cancellationToken);
                return Results.Ok(new Page<AuditEvent>(page.Items, page.NextCursor));
            }
            catch (PersistenceConflictException e) when (e.Code is "invalidCursor" or "cursorExpired")
            { throw new ApiFailure(e.Code == "cursorExpired" ? 410 : 400, e.Code, "The cursor is invalid or expired."); }
        }).RequireAuthorization("audit.read");
}
