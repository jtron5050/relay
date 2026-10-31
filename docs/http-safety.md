# HTTP safety conventions

Issue #4 implements the HTTP foundation in both hosts. Only `/health` and
`/openapi/v1.json` are public routes today; the feature routes in api-v1.md remain
planned. Host-specific OpenAPI files describe this implemented surface and the
shared error schema and limits. Extend them when adding each feature.

`src/SharedApi` is linked into each API assembly to share HTTP behavior without
putting transport concerns in Core or Infrastructure. Both hosts register strict
camelCase JSON (unknown properties and numeric enum values rejected), UTC
DateTimeOffset timestamps ending in Z, and a 256 KiB request limit. Middleware
also counts streamed bodies, covering hosts without Kestrel's size feature.
`/health` remains unauthenticated. No protected feature routes are added here.

Errors use `{code,message,correlationId,retryable}`. Valid UUID
`X-Correlation-Id` values are echoed; invalid/missing values generate a UUID.
Responses use `Cache-Control: no-store`. Empty error responses and exceptions use
safe messages in Development and Production. Unclassified dependency failures
return retryable 503, never application removal instructions. Exception text,
request bodies, and credentials are not logged. 429/503 and explicitly retryable
conflicts include `Retry-After: 5`. Resource handlers must return the same 404 for
missing and inaccessible resources; 403 is reserved for known permission denial.
Authentication implementations remain in later issues.

Future handlers must call `RequireOpaqueId`, `RequireRevision`,
`RequireIdempotencyKey`, `Pagination`, and `ValidateBatch` as applicable.
Pagination rejects unknown/duplicate queries, defaults to 50, and caps at 100;
telemetry batches default to at most 50. These are reusable validators, not global
requirements on `/health`, nor implementations of storage concurrency or receipts.
Use `If-Match: "rev-7"` for mutable operations: absent is 428, malformed is 400,
and handlers return 409 `revisionConflict` when the authoritative revision differs.
First assignment creation uses `If-None-Match: *` in its future handler.
Idempotency keys are 16–128 printable ASCII characters; feature handlers
must bind receipts to actor, operation, and canonical body per ADR 0001. Validation
alone does not make a mutation idempotent.

`CursorCodec` authenticates and encrypts positions using ASP.NET Data Protection,
expires tokens after 15 minutes, and rejects tokens over 4 KiB. Callers must bind
scope to API/actor, resource, filters, limit, and query version; authorize every
page independently. Invalid, tampered, wrong-scope and expired tokens return 410;
oversized tokens return 400. Persist and protect a shared Data Protection key ring
and stable application name per host/environment before multi-instance deployment;
ephemeral keys invalidate cursors on restart. Do not expose DynamoDB keys directly.
Response/page work budgets and collection-specific limits from api-v1.md remain
the responsibility of the later feature handlers.

Validation: shared integration tests run against both real hosts in Development
and Production, using test-only middleware probes (not shipped routes). They cover
status/error safety, malformed/unknown-field JSON, oversized requests, correlation,
OpenAPI, header validation, pagination bounds, and cursor tampering/scope/expiry.
No AWS services or credentials are required. Use the README build/test commands;
this cloud workspace requires writable CLI/cache/runtime/temp directories under
`/workspace/work` and .NET 10 SDK 10.0.403 installed under `/workspace/dotnet`.

Verified on 2026-10-31 with SDK 10.0.403:

- `dotnet restore Relay.slnx`: passed (NuGet network access required).
- `dotnet build Relay.slnx -c Release --no-restore -m:1 --disable-build-servers`:
  passed, zero warnings/errors.
- `dotnet test Relay.slnx -c Release --no-build --no-restore -m:1 --disable-build-servers`:
  passed all 10 tests across both hosts.
- Started both Release Kestrel hosts on ports 5100/5200 and verified `/health`,
  `/openapi/v1.json`, and safe correlated `/v1/missing` 404 responses; stopped both.
- `git diff --check`: passed.
