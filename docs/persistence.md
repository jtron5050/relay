# DynamoDB persistence foundation

Issue #5 implements the ports in `Relay.Core/Persistence` and the AWS adapter in
`Relay.Infrastructure/Persistence`. It adds no HTTP operations and does not yet
wire either API host to storage. Feature handlers must implement their own
permission, validation, authorization-vector and audit rules from
[ADR 0001](adr/0001-v1-storage-and-consistency.md).

## Configuration and deployment

Register `services.AddRelayPersistence(configuration)` with:

```json
{"DynamoDb":{"TableName":"relay-dev","Region":"us-east-1"}}
```

Production uses the standard AWS SDK credential chain and regional HTTPS. Supply
least-privilege GetItem, Query, TransactGetItems and TransactWriteItems permissions
on the configured table and Query on its indexes. No table is provisioned by this
slice; provisioning belongs to #6. The table requires string PK/SK, sparse string
GSI1PK/GSI1SK and GSI2PK/GSI2SK indexes, and optional TTL on numeric `expiresAt`.
Indexes may use the ADR's restricted projections: the adapter rehydrates listed
rows by strongly reading their base keys. Index membership remains eventual and
is never proof of authorization. Configure encryption/PITR/Streams in provisioning.

Configure ASP.NET Data Protection with a durable shared encrypted key ring and a
stable application name for every instance serving the same cursors. The adapter
uses its own versioned purpose. Default ephemeral/container key rings invalidate
cursors on restart. Never put keys in source control. Inject TimeProvider for tests.
`LocalEndpoint` is an explicit loopback HTTP override with dummy credentials;
never use it for deployed AWS access.

## Calling conventions

- Keys use the ADR's layouts; `PhysicalKeys.Component` escapes individual IDs
  before joining with `#`. Number/timestamp/shard helpers preserve ADR ordering.
- Stored records have schemaVersion 1, UTC metadata, positive integer revision,
  sparse index attributes and JSON `payload`. Do not store secrets in payloads.
  Feature adapters map their domain fields into this versioned envelope.
- PutRecord with expected revision 0 reserves an absent identity, including
  uniqueness reservations and lock rows. Updates require expected+1. Use retained
  tombstones and revisions when releasing/reacquiring slots; no delete primitive
  permits identity/revision reuse. CheckRevision(0) checks absence. Revisions are
  item-level: independent revision boundaries require separate keyed records;
  future feature adapters needing atomic field conditions must add that explicitly.
- A transaction has 1–100 distinct keys, conservatively serialized items below
  300 KiB and a conservative 4 MiB request budget. It is never split. AWS also
  enforces actual item/transaction limits, including existing-item sizes.
  TransactGet preserves missing entries and request order. Conditional failures
  raise `revisionConflict`; transaction contention raises `transactionConflict`.
  Dependency/throttling/unknown failures remain SDK exceptions, not fake conflicts.
  Callers retry boundedly by rereading/recomputing the complete business operation.
- Query requires actor/scope, exact partition and optional sort prefix or inclusive
  lower/upper sort-key range. One call
  evaluates at most 100 rows and one DynamoDB 1 MiB page. No filters, scans or
  hidden fill loops. The encrypted/authenticated cursor binds the complete query,
  index, direction, scope and limit, and expires after 15 minutes. Tampering or
  query changes return `invalidCursor`; logical expiry returns `cursorExpired`.
  Shard-major orchestration belongs to the owning feature; each feature must
  enforce the ADR's four-shard/200-row/1 MiB total request budget. Current primitives
  deliberately query a single partition. GSI results may omit/repeat under writes.

## Idempotency and secrets

`ExecuteOnceAsync` hashes actor, operation and key separately. Operation must include
HTTP method, route and **all target IDs**. Pass the validated input, relevant
preconditions and normalized defaults as CanonicalInput. Canonical JSON v1 sorts
object properties recursively, preserves arrays, rejects duplicate properties,
and preserves numeric representations. Normalize semantic numbers/timestamps in
validated DTOs first; exclude correlation IDs. The body itself is never persisted
in a receipt. Receipt identity and request hash are separate.

The successful receipt and business actions commit in one transaction. Check a
receipt before evaluating stale revisions. Same scoped key/content replays the
original safe status/reference; changed content returns `idempotencyConflict`.
Authenticate and authorize every call, including replay. The safe result DTO has
no arbitrary body or header fields: pass only an opaque resource/operation ID,
never tokens, diagnostics or URLs. The status is limited to successful 2xx.
Use SecretIssued for a one-time secret response: the primitive returns safe
metadata on first success, and `secretAlreadyIssued` on replay. The exception includes only
the safe resource reference for the handler to use for dedicated authorized recovery; this slice
does not implement enrollment or rotation recovery (#10–12). Never pass the secret
to this primitive, even as the reference. Upload URLs must be regenerated only
after fresh authorization by their owning handler.

Ordinary receipts logically expire after 24 hours; retained expired rows reject
reuse with `idempotencyExpired`. TTL is cleanup only. After physical deletion the
retry guarantee ends. Permanent attempt/allocation bindings omit TTL. Do not
switch retention mode under an existing scoped key. Time-dependent locks and
credentials must explicitly validate deadlines in their feature transaction;
TTL never releases locks or grants authorization. Unknown commit outcomes should
be resolved by rereading/replaying the receipt with exactly the original input.

## Reproducible validation

Use .NET 10 and Docker. This command starts an isolated, ephemeral DynamoDB Local
3.1.0 service without AWS credentials or cloud resources:

```sh
docker run --rm -d --name relay-dynamo -p 127.0.0.1:8000:8000 amazon/dynamodb-local:3.1.0 -jar DynamoDBLocal.jar -inMemory -sharedDb
dotnet restore Relay.slnx
dotnet build Relay.slnx --configuration Release --no-restore
RELAY_DYNAMODB_ENDPOINT=http://localhost:8000 dotnet test Relay.slnx --configuration Release --no-build --no-restore
docker stop relay-dynamo
```

Tests create/delete a unique table on a loopback endpoint using dummy credentials;
never point them at production. Integration tests are explicitly skipped without
RELAY_DYNAMODB_ENDPOINT. CI always starts the service and sets the endpoint, so
skipped local tests are not evidence of persistence validation. Local covers real
service transaction cancellation, concurrent writers, atomic rollback/reservations,
transactional reads, sparse-index hydration, pagination, cursor scope/expiry,
receipt concurrency/replay/conflicts/expiry/permanence, secret replay, and budgets.
It does not establish production IAM, GSI lag, TTL scheduling, throughput or AWS
failure behavior; those remain isolated-AWS validation gates in later slices.
