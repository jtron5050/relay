# Administrative audit history

Issue #8 implements GET `/v1/audit-events` with the `audit.read` permission.
Administrator, ReleaseOperator and SupportViewer can read history. No production
mutation endpoint is introduced by this slice.

Supply `from` (inclusive) and `to` (exclusive) as UTC timestamps ending in Z,
with a nonempty interval no longer than 31 days. Optional `targetType` and
`targetId` must appear together. Other filters, including actor filters, are
rejected. `limit` defaults to 50 and ranges from 1 to 100; `cursor` continues
with exactly the same query and authenticated subject. Cursors expire after
15 minutes and are limited to 4 KiB. Invalid cursors return 400 invalidCursor;
expired cursors return 410 cursorExpired.

Pages are ordered by ascending UTC day (month for target history), then shard
0–15, then ascending timestamp/event ID within a shard. This is not global time
ordering or a frozen snapshot. Target history uses eventual GSI1 membership;
clients should deduplicate event IDs. Empty pages can have a nextCursor. Each
request queries at most four partitions, stopping at the first evaluated row
page; at most 100 rows and one nonempty DynamoDB service page are evaluated.

## Future mutation integration

Inject IAdministrativeMutations, authenticate and authorize the operation on
every invocation, and construct AuditContext from the validated staff sub and
server correlation ID. IdempotencyRequest.Actor must equal that sub. Include
HTTP method, route and all target IDs in Operation; normalize validated input
and revision preconditions into CanonicalInput. Never include correlation IDs
or secrets there.

Pass a deferred callback that reads authoritative state, validates preconditions,
and returns AuditedMutation containing conditional business actions, a safe
result reference, and AuditDescription. Receipt lookup runs before this callback,
so successful retries bypass stale revision validation. The coordinator commits
business changes, one immutable audit and the receipt in one transaction.
Budget two extra transaction items for audit and receipt. It never splits writes.
The caller must map persistence conflicts to safe HTTP errors and revalidate
permissions on replay. Unknown commit outcomes are resolved by replaying the same
request, not by inventing another idempotency key.

Summaries are developer-authored descriptions (up to 1024 characters), optionally
naming allowlisted fields. Do not pass user-entered values, request/response
bodies, credentials, authorization headers, signed URLs, or exception messages.
Before/after revisions are positive or null. Events use server-generated IDs and
UTC times. Denied or failed requests do not create successful-change audits.
Audit rows have no TTL; archive and expiration workers remain future work.

## Configuration and verification

Admin now requires DynamoDb:TableName, with optional Region and loopback-only
LocalEndpoint as described in persistence.md. Configure a shared durable encrypted
Data Protection key ring and stable application name before multi-instance use.
AWS credentials follow the standard SDK chain; no credentials belong in source.
Health and OpenAPI remain anonymous and make no storage calls.

Both hosts suppress raw framework request URL, authentication, IdentityModel and
AWS diagnostic categories. Shared error logs retain only status and correlation,
without exception text. Do not enable request-body/header or SDK wire logging.

Run the Release build and full solution tests with RELAY_DYNAMODB_ENDPOINT set
to an isolated DynamoDB Local instance. Local integration tests prove transaction
behavior, not production IAM, eventual-index timing or AWS failure handling.

Local verification used .NET 10.0.403 and Node.js 22.23.4 from `/tmp`, with
DOTNET_CLI_HOME and JSII_RUNTIME_PACKAGE_CACHE pointing to writable `/tmp`
directories. The system snap launcher and default jsii cache were unusable in
this sandbox. Restore and Release build succeeded without warnings. Tests used
DynamoDB Local 3.1.0 on loopback port 8000. Kestrel checks returned 200 for health
and OpenAPI, and safe correlated 401 for unauthenticated audit history.

Self-review covered conditional transaction boundaries, receipt lookup before
revision checks, concurrent replay, query boundaries/cursor budgets, permissions,
and secret logging. It identified and fixed a concurrent commit occurring between
receipt lookup and stale-revision validation; a regression test covers that order.
Human review is required before merge because this introduces the shared audit
transaction hook and changes diagnostic logging across both API hosts.
