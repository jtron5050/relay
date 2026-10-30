# ADR 0001: v1 storage and consistency

Status: accepted for implementation, not yet implemented

Date: 2026-10-30

Issue: [#3](https://github.com/jtron5050/relay/issues/3)

Basis: [product specification v1.0](../product-spec.md), especially §§3–6

Companion: [v1 HTTP contract](../api-v1.md)

## Context and decisions

Relay needs explainable update decisions under concurrent administrative edits,
retry-safe agent traffic, and frozen deployment membership larger than a DynamoDB
transaction. This record resolves the blueprint's recommendations for v1. It does
not implement persistence, authentication, routes, infrastructure, or workers.
The existing public `/health` endpoints remain the only implemented API surface.

1. Use Cognito user-pool **access tokens for staff**, with issuer, token use,
   expiry, allowed `client_id`, scopes and role/group validation. Require MFA for
   privileged staff through pool policy. Roles are Relay permissions, not merely
   successful JWT authentication. Staff access administration is a Cognito
   administrative workflow; v1 exposes no staff-management HTTP endpoints.
2. Use independent, random 256-bit per-computer bearer credentials. Persist only
   SHA-256 hashes and credential generation/expiry metadata; never reversible
   credentials in DynamoDB. Bootstrap tokens use the same entropy/hash approach.
   High entropy makes offline guessing infeasible without a password KDF. The
   credential's computer/customer binding is immutable. Disable authorizer caching.
3. Use one regional operational DynamoDB table, on-demand capacity, encryption,
   PITR, TTL and Streams with old/new images. Two sparse, overloaded GSIs serve
   operator listings. Single-region writes are the v1 consistency boundary; do not
   introduce active-active global-table authorization.
4. Use private, encrypted, versioned S3 for opaque Windows packages. Block public
   access. Store exact bucket/key/**version ID**, byte size, SHA-256, architecture,
   file version, signer metadata and declarative installer hints in a verified
   manifest. Published manifests and referenced bytes are immutable. Upload roles
   cannot overwrite/delete published versions; new uploads use fresh staging
   keys. Do not rely on ETag as SHA-256. Signing remains outside Relay.
5. Keep domain rules and persistence ports in `Relay.Core`, AWS adapters, key
   encoding and workers in `Relay.Infrastructure`, and HTTP DTOs, headers and
   authorization in the respective API host. This document's DTOs are design
   contracts, not shared HTTP types to add to Core.

Confirmed product behavior is preserved: Windows x64/ARM64, separate APIs,
Cognito staff authorization, DynamoDB, manual promotion, no automatic current
selection, no fallback from a selected withdrawn/incompatible release, no implicit
downgrade, and no customer self-service, web UI or production Windows agent.

## Physical keys and bounded collections

The table has string `PK`, `SK`, `entityType`, `schemaVersion` and UTC timestamps.
Mutable records have integer revisions starting at 1; never reuse an identity or
revision after deletion. IDs are opaque random identifiers. Encode components
unambiguously (reserved delimiters escaped); fixed-width unsigned numbers and
UTC timestamps sort lexically. The following keys are physical contracts, not
client-visible IDs. `s32(id)` is the unsigned first 32 bits, big endian, of
SHA-256 of UTF-8 ID modulo 32; `s16` similarly uses 16. Shard counts and hash
version are stored in parent metadata and never changed in place.

| Record | PK | SK | Notes |
| --- | --- | --- | --- |
| Customer | `CUSTOMER#c` | `META` | Customer identity is immutable; no v1 customer move/delete. |
| Application | `APP#a` | `META` | `policyRevision` changes when update policy changes. |
| Release | `APP#a` | `RELEASE#r` | Bounded manifest: at most two architecture variants. |
| Version uniqueness reservation | `APP#a` | `VERSION#normalizedSemVer` | Points to release; retained permanently, including withdrawal. |
| Channel | `APP#a` | `CHANNEL#ch` | Current release, rollback intent, active slot, `policyRevision`. |
| Channel roster gate | `APP#a` | `ROSTER-GATE#ch` | Assignment membership revision and snapshot fence. Separate from policy revision. |
| Current-release history | `APP#a#CHANNEL#ch` | `CHANGE#time#eventId` | Immutable change, actor and rollback reason. |
| Computer | `COMPUTER#pc` | `META` | Customer, enabled/revoked state, `authRevision`; ≤10 active assignment app IDs/count; separate inventory/last-seen fields. |
| Assignment | `COMPUTER#pc` | `ASSIGNMENT#a` | Channel, optional pin and rollback intent; `revision`; tombstone on removal. |
| Authoritative roster entry | `ROSTER#a#ch#c#s32(pc)` | `COMPUTER#pc` | Written transactionally with assignment; pin changes do not alter membership. |
| Inventory | `COMPUTER#pc` | `INVENTORY` | Full bounded snapshot, agent revision and canonical payload hash. |
| Attempt counter/current progress | `COMPUTER#pc` | `PROGRESS#a` | Latest allocated number/ID and latest progress; distinct from inventory. |
| Credential lookup | `CREDENTIAL#sha256` | `META` | Computer ID, generation, validity bounds and revoked flag. |
| Enrollment token lookup | `ENROLLMENT#sha256` | `META` | Customer, expiry, consumption/request binding, recovery deadline. |
| Enrollment metadata by ID | `ENROLLMENT-TOKEN#id` | `META` | Safe administrative metadata and internal token-lookup reference; kept in sync with lookup. |
| Enrollment request binding | `ENROLLMENT-REQUEST#sha256(requestId)` | `META` | Token hash and computer ID; cannot authenticate or reveal a credential. |
| Upload session | `APP#a#RELEASE#r` | `UPLOAD#id` | Staging key, multipart state, expected manifest, verified version and expiry. |
| Background operation | `OPERATION#id` | `META` | Kind, actor/resource binding, state, deadline, lease and result reference; Streams dispatches work. |
| Deployment | `DEPLOYMENT#d` | `META` | Application/channel, release, selected customer IDs, lifecycle and revision. |
| Snapshot generation | `DEPLOYMENT#d` | `SNAPSHOT#g` | Build state, lease generation, totals, manifest pointer and cut time. |
| Snapshot build checkpoint | `DEPLOYMENT#d#BUILD#g` | `PART#customer#rosterShard` | Last evaluated roster key, page ordinal, count and done flag. |
| Snapshot page receipt | `DEPLOYMENT#d#BUILD#g` | `PAGE#customer#rosterShard#ordinal` | Page digest/count; guards replay and counter updates. |
| Snapshot completion/histogram | `DEPLOYMENT#d#BUILD#g` | `DONE#level#group` or `HISTOGRAM#shard` | Bounded completion tree and 32 bounded groups of bucket counts. |
| Snapshot target | `DEPLOYMENT#d#GEN#g#SHARD#s32(pc)` | `COMPUTER#pc` | Immutable membership/bucket, mutable outcome projection. |
| Offer | `COMPUTER#pc` | `OFFER#o` | Application/release/package, source, revision vector, request inventory and expiry. |
| Attempt | `ATTEMPT#t` | `META` | Permanent binding, assigned number, source, latest accepted state/sequence. |
| Authoritative attempt directory | `COMPUTER#pc#ATTEMPTS#UTC-month` | `CREATED#time#a#number#t` | Immutable reference written with allocation; supports complete projection repair without GSI enumeration. |
| Attempt event | `ATTEMPT#t` | `EVENT#sequence` | Immutable payload/hash and event ID; sequence is unique per attempt. |
| Event ID deduplication | `COMPUTER#pc#EVENT#eventId` | `META` | Attempt/sequence and canonical payload hash. |
| Idempotency receipt | `IDEMPOTENCY#actorHash#operationHash#keyHash` | `META` | Request hash, result reference/status, logical expiry; no credentials/URLs. |
| Audit event | `AUDIT#UTC-day#s16(eventId)` | `time#eventId` | Immutable actor, action, target, correlation ID and safe change summary. |
| Summary generation/shard | `DEPLOYMENT#d#SUMMARY#g` | `META` or `SHARD#n` | Generation pointer is on deployment; watermarks and as-of time. |
| Summary contribution | `DEPLOYMENT#d#SUMMARY#g#SHARD#s32(pc)` | `COMPUTER#pc` | Last applied target projection version and counted categories. |

No unbounded arrays of targets, attempts, events or audits live on parent items.
Keep items under 300 KiB (DynamoDB's hard limit is 400 KiB); keep transactions
below both 100 distinct items and 4 MiB. A request exceeding either budget is
rejected or scheduled as a job, never silently split into partial business writes.

### Index definitions

Both GSIs use string partition/sort attributes `GSI1PK/GSI1SK` and
`GSI2PK/GSI2SK`. Only the following entity types populate them. Project base keys,
IDs, display name, lifecycle state, relevant revision and timestamps into GSI1;
add latest attempt/outcome and last-seen timestamps to GSI2. Fetch details by base
key. No secrets, hashes, diagnostics, manifests or URLs are projected.

| Source | GSI1PK | GSI1SK | Purpose |
| --- | --- | --- | --- |
| Customer META | `CATALOG#CUSTOMER#s16(c)` | `CREATED#time#c` | Customer catalog. |
| Application META | `CATALOG#APP#s16(a)` | `CREATED#time#a` | Application catalog. |
| Computer META | `CUSTOMER#c#COMPUTER#s32(pc)` | `CREATED#time#pc` | Customer fleet. |
| Deployment META | `APP#a#DEPLOYMENT#s16(d)` | `CREATED#time#d` | Application deployment history. |
| Attempt META | `COMPUTER#pc#ATTEMPT#UTC-month` | `CREATED#time#a#number#t` | Computer history with a timestamp sort-key range inside each month. |
| Audit event | `AUDIT-TARGET#type#id#UTC-month#s16(eventId)` | `time#eventId` | Resource audit/history. |

| Source | GSI2PK | GSI2SK | Purpose |
| --- | --- | --- | --- |
| Computer META | `CUSTOMER#c#STATE#enabledOrRevoked#s32(pc)` | `LASTSEEN#time#pc` | Fleet state/last-seen view; never authentication. |
| Snapshot target | `DEPLOYMENT#d#GEN#g#OUTCOME#state#s32(pc)` | `COMPUTER#pc` | Outcome view; state is pending/offered/downloading/installing/rebootRequired/succeeded/failed. |

Attempt numbers are scoped to computer/application; history across applications
is traversed by month/shard, not claimed to be one global chronological stream.
Staleness is computed from last-seen and expected interval; it is not an index
state that would require time alone to mutate records. There is no arbitrary
text search or arbitrary combination of server-side filters in v1.

### Complete §6 access-pattern mapping

`Get`/`Query` below means base-table `ConsistentRead=true` when authoritative.
BatchGet with strong reads is useful for discovery, but is not an atomic snapshot.
Every query supplies a partition key, key condition, evaluated-item limit and
continuation key. No normal endpoint or worker requires `Scan` or PartiQL scans.

| Product access pattern | Exact path and index | Bound/pagination and authority |
| --- | --- | --- |
| Authenticate/revoke computer | Get credential hash and computer META; transaction checks validity/generation/authRevision. Revoke conditionally updates META plus audit/receipt. | Two keyed reads; never use fleet GSIs. Computer revocation invalidates all credentials without enumerating them. |
| Evaluate update | Get computer, ≤10 assignments, APP/CHANNEL policies, active deployment META, one target per application, selected release and inventory. Final transactional read/write protocol below. | Evaluate each application separately; ≤10 dependency items per application, ≤3 retries. No global index or search for newest release. |
| List customer computers | GSI1 `CUSTOMER#c#COMPUTER#shard`; state view uses GSI2. | Traverse 32 shards with cursor; eventual listing, then keyed detail reads. Stale queries use a last-seen sort-key range. |
| List releases/channels | Query `APP#a`, prefix `RELEASE#` or `CHANNEL#`. | Page size ≤100; no post-query lifecycle filter. Clients can filter bounded pages. |
| List deployment targets/outcomes | Query committed generation's 32 target shards; outcome filter uses GSI2. Get summary generation/shards for counts. | Page/shard cursor; base membership authoritative, GSI outcomes and summaries eventual. No client-supplied generation can expose uncommitted targets. |
| Inspect installation history | GSI1 computer/month, then Get ATTEMPT META and Query `ATTEMPT#t` prefix `EVENT#`. | History range ≤31 days/request; events page ≤100. Month traversal cursor. Validate computer binding on detail reads. |
| Unique product version | Conditional Put `APP#a / VERSION#normalizedSemVer` with release creation. | One reservation; SemVer normalized excluding build metadata, case-sensitive prerelease identifiers. No GSI uniqueness check. |
| One activated nonterminal deployment | Conditional update channel's active slot plus deployment state. Slot includes building/active/paused generation. | One keyed lock; completed/cancelled release it transactionally. Drafts do not hold the slot. |

Supporting HTTP paths are also bounded: customer/application catalogs use GSI1's
16 shards; deployment lists require an application ID and use 16 shards; assignment
lists query one computer prefix (maximum ten live entries); credential IDs are
stored in bounded computer metadata (at most two); enrollment and idempotency are
direct hash lookups; current history is an application/channel prefix query;
global audit uses day/16-shard queries and target audit uses GSI1 month/16-shard
queries. Snapshot workers query the explicit customer × roster-shard manifest.
Cleanup/rebuild/archive workers use known generation manifests, time buckets or
TTL/Streams, not table scans. Full disaster restoration may use AWS backup/export
tools outside normal API paths.

The computer's bounded active-assignment ID set is maintained in the assignment
write transaction with its count and a separate `assignmentSetRevision`. Update
checks discover assigned applications from that set, then read each assignment
authoritatively; they never query an unlimited history of assignment tombstones.
The assignment-list endpoint can include tombstones with normal pagination.
Never-seen computers use an index sentinel preceding real timestamps; staleness
is based on enrollment time until the first server-observed check-in.

### Cursor semantics

List responses return `{items, nextCursor}`; null means traversal finished.
Default limit 50, maximum 100; each call evaluates at most 200 rows, 1 MiB of
DynamoDB pages and four shard partitions, then returns even if few/no items match.
Never fill a page by looping over an entire sparse index. The opaque, signed,
versioned cursor binds route, authorized scope/actor, normalized query, direction,
time range, limit, index/generation/shard count, current shard and exclusive last
evaluated key. It expires in 15 minutes. Reject tampering, scope/query changes
and unsupported versions with `invalidCursor`; do not accept raw DynamoDB keys.

Traversal is shard-major, sort-key ordered within a shard; v1 does not promise
global timestamp ordering. Only store the active shard's key, not all shard keys.
The cursor stays at that shard until exhausted, then advances. GSIs can omit or
repeat entries under concurrent changes; lists are views, not frozen snapshots.
Clients deduplicate IDs; authoritative detail/authorization reads revalidate.
Snapshot target membership is immutable after commit, so its base-table traversal
has stable membership even while outcome fields change. A summary generation swap
invalidates summary-specific cursors with `cursorExpired`.

## Revision boundaries and authorization

Separate revisions prevent heartbeat traffic from making all policy reads fail:
computer `authRevision` covers enabled state and credential generation;
assignment `revision` covers channel/pin/removal; application `policyRevision`
covers update policy; channel `policyRevision` covers current selection/rollback
and active deployment pointer; deployment `revision` covers lifecycle/percentage;
release `revision` covers draft edits/publication/withdrawal. Roster-gate revisions,
inventory revisions and attempt sequences are independent. Last-seen/projections
never change an authorization revision. Tombstoned assignments keep their revision
so remove/recreate cannot create an ABA race.

The Admin resource ETag uses a separate resource `revision` wherever a record has
more than one revision. Every staff edit checks/increments that resource revision
along with the affected policy/auth revision; worker lifecycle changes do too.
Heartbeats and projections do not change Admin ETags. Channel display-name edits
do not change policyRevision, but still conflict with concurrent staff edits
through the resource revision. Internal slot/fence changes use field conditions
and never overwrite unrelated channel attributes.

`policyRevision` on an offer is an **opaque versioned token for a revision vector**,
not one counter shared by the whole fleet. The blueprint's numeric example was
illustrative. Persist computer auth, assignment, app, channel, deployment (if
used), snapshot generation and release revisions plus source ID; encode/sign the
token for the response. Do not reveal physical keys. No revision token itself
grants access.

### Update/refresh/attempt authorization protocol

1. Authenticate the computer using a strong credential and computer read and
   server time, including credential generation, explicit expiry, revocation and
   enabled state. Agent body IDs never choose the authenticated identity.
2. Discover dependencies by key. Use `TransactGetItems` for the entire per-app
   read set, including credential, computer, assignment, app, channel, deployment,
   target, selected release and inventory as needed. Check that all references
   still match discovery; if a pointer changed, restart with the new read set.
   Absence is an input too (assignment tombstone, empty current/active pointer,
   or missing target); it must be in the read/check set when it affects selection.
3. Core selects pin, admitted active deployment, then current. A selected release
   must be published, compatible and SemVer eligible; selection failure does not
   fall back. Missing/corrupt dependencies fail closed with a retryable error.
   An absent application can install; unknown installed product version blocks
   offers; equal SemVer precedence is up-to-date regardless of file version.
4. For an offer, commit a `TransactWriteItems` with conditions on the captured
   authorization revisions/references and credential validity, immutable target
   generation/bucket when used, and an offer Put. Use that commit as the
   authorization linearization point. Consolidate conditions on an item that is
   also updated; DynamoDB forbids two transaction actions on the same item.
   Conflicts retry discovery at most three times, then return retryable
   `policyChanged`, never an offer from mixed revisions. No-offer decisions are
   valid at the transactional read snapshot; they do not mutate policy.
5. Sign the exact S3 version after commit. URL expiry is bounded by authorization
   time + ten minutes, credential expiry and the offer lifetime. Recheck if that
   window has elapsed before responding. Concurrent pause/revocation/withdrawal
   can linearize after issuance; already authorized URLs cannot be recalled.
   No positive authorization cache extends this boundary.

Download refresh and attempt allocation repeat this protocol. They require the
offer's **same source, application, release and package** to remain the selected,
authorized policy. Promotion/current revisions may be newer if that exact source
is still eligible; record the new vector. Never convert a paused deployment offer
to a pin/current offer: return `offerNoLongerEligible`, and let a new update check
issue an independent offer. Offer IDs and client-supplied revision tokens alone
cannot authorize a download or installation.

### Transaction recipes

Every administrative mutation includes an immutable audit Put and an idempotency
receipt Put in the **same transaction** as the business change. Audit failure
fails the mutation. Conditional failures map to conflicts rather than silent
overwrite. Foreign-key existence/state is checked by base key in that transaction.

| Operation | Atomic writes/conditions |
| --- | --- |
| Create customer/app/channel | Put new identity, required parent condition, initial revision/gate where applicable, audit and receipt; no physical deletion in v1. |
| Assignment create/change/remove | Check computer/app/channel, expected assignment revision (or absence on first create); Put revised assignment/tombstone, delete old and put new roster entry as necessary, increment/check old/new channel roster gates with no build fence, audit and receipt. Pin-only changes check policy but do not change roster. No-op/same-channel writes do not delete and Put the same key; consolidate into one action. Max ten live assignments enforced with a conditioned count on computer META. |
| Set/clear pin | Expected assignment revision; check published same-app release for a pin, explicit rollback permission/reason if requested; update assignment, audit and receipt. No deployment progress mutation. |
| Draft release/version | Reserve normalized version permanently on draft creation and Put draft together. Product version is thereafter fixed; change it by creating another draft. Package/notes draft edits check revision and draft state. |
| Verify/publish | Verify exact S3 version asynchronously; condition draft revision/upload generation and persist verified manifest. Publish transaction checks same verified generation/digests, draft revision/state, freezes manifest, audit and receipt. No cross-service transaction: orphan uploads are harmless; no publish from an incomplete job. |
| Withdraw release | Condition published revision; set withdrawn and increment revision, audit and receipt. Do not rewrite pins/current/targets or free version reservation. |
| Set/clear channel current | Condition channel policy revision; check selected same-app published release (unless clearing); update pointer/rollback intent and revision, Put immutable current-history record, audit and receipt. No deployment action alters current. |
| Deployment activate | Reserve channel slot/fence and build generation; publish active state only through the snapshot protocol below. Check release published both at start and final commit. |
| Promote/pause/resume/complete/cancel | Check deployment revision/state and channel slot owner; update lifecycle/percentage and revisions; release slot on terminal state, audit and receipt. Percentage only increases (integer basis points). Pause preserves snapshot/buckets. |
| Revoke/rotate | Revoke updates computer authRevision/state with audit/receipt. Rotation creates new hash mapping and bounded previous-generation overlap, conditions current generation, increments authRevision, audit/receipt. No enumeration or GSI lookup. |
| Enrollment | Check hash token unexpired/unconsumed, customer exists, request binding absent; consume token/update token-ID metadata and create computer, credential hash, request binding, receipt and security audit atomically. No assignments on enrollment. |
| Attempt allocation | Revalidate offer/policy; condition previous PROGRESS number, increment number and set latest attempt, Put attempt binding, authoritative directory reference and permanent receipt. Replay returns the same ID/number. Inventory is untouched. |
| Installation event | Check authoritative attempt binding; Put sequence record and event-ID dedupe together, condition attempt progress revision and transition; update attempt and, only if latest allocated, computer/application PROGRESS; update deployment target projection only for deployment-source attempts. |
| Check-in inventory | Condition computer and inventory revision/hash; replace full inventory only with higher revision. Same revision/hash is harmless; same revision/different hash conflicts; lower revision is acknowledged stale with no inventory change. Last-seen uses monotonic server receipt time. |

Only deployment-source attempts count toward that deployment. Pin/current attempts
for the same release stay separately attributable. Each target chooses its latest
deployment-bound attempt by number then sequence, so a delayed failure of attempt
41 cannot replace success of 42. Events for an older attempt are still stored;
their valid attempt-local history can advance, but they cannot update newer
current/target status. On conditional conflict, reread and recalculate the whole
event transaction; never silently drop a historical event.

Within an attempt, permit `offered → downloading → installing → rebootRequired →
succeeded|failed`, with direct forward skips (including failure from any
nonterminal state) for offline buffering. `rebootRequired → installing` is allowed
after reboot with a higher sequence. Terminal succeeded/failed never change.
Lower sequence events are retained without changing progress if consistent with
the accepted higher-sequence history; contradictory histories/terminal outcomes
are rejected. Validate against neighboring retained events, not arrival order.
Query at most one predecessor and successor by sequence; every insertion,
including historical ones, increments an attempt `eventWriteRevision`. Condition
that revision in the insertion transaction so concurrent neighbor changes force
revalidation even when the visible latest progress does not advance.
Identical event-ID or attempt/sequence replays have no effects; conflicting reuse
returns 409 even if the supplied event ID differs. The server assigns the attempt
number; neither device time nor a supplied attempt number orders progress.

### Idempotency, secrets and audit

Use a required `Idempotency-Key` for every administrative mutation and attempt
creation. Scope it to authenticated actor (staff subject or computer ID), HTTP
method and route template **including target IDs**. Canonicalize the validated
body and relevant preconditions; store a SHA-256 request hash. Same key/hash
returns the original status/result reference, same key/different input returns
409 `idempotencyConflict`. Check successful receipts before rejecting a now-stale
If-Match so a lost successful response is replayable. Replays still require
current authentication/permission; never replay secret fields or stale URLs.

Receipts last 24 hours by default using explicit logical expiry; DynamoDB TTL
only cleans up. Once expired, reuse is rejected with `idempotencyExpired` while
the record remains; after physical deletion the retry guarantee ends. Attempt
creation receipts are an exception: retain their keyed request/result binding
permanently without TTL, along with attempt metadata, so the same attempt key
can never allocate a second number. Replaying allocation does not renew the
attempt's ingestion deadline or authorize restarting a terminal installation.
Enrollment's permanent request binding likewise prevents a second identity.
Hash canonicalization excludes correlation IDs and is versioned. Long jobs save
a stable operation/resource ID in the initial transaction and replay that ID;
workers use page/generation receipts, not a long-lived DynamoDB client token.

Never store plaintext bearer credentials, enrollment secrets or presigned URLs
in receipts/audits. For lost enrollment responses, an authenticated recovery
request must prove possession of the original bootstrap secret and the bound
enrollment request ID within ten minutes of consumption. Atomically replace the
credential hash/generation and invalidate its predecessor before returning a new
random secret; serialized retries may replace it again, never create a computer.
After the window, an administrator revokes the abandoned computer and issues a
new token. A request ID alone never releases a secret. Token issuance and staff
credential rotation similarly use a one-time response; a lost token is revoked
and replaced, and a lost rotated credential requires another authorized rotation.

For a successful secret-producing request whose response was lost, replay returns
409 `secretAlreadyIssued` with only the safe resource/recovery reference. It does
not rotate again under the same idempotency key. Upload URL requests are different:
replay can regenerate a fresh URL for the same session only after authoritative
draft/session authorization; no URL is stored in a receipt.

Audit attributes: event ID, actor kind/subject, operation, target type/ID,
server timestamp, correlation ID, before/after revision and safe summary/reason.
Security enrollment/rotation/revocation writes also have transactional audit.
Job start and completion/failure each have audit records. Denied requests go to
sanitized operational logs, never a fake successful change audit. Retain audit
online at least 365 days; set TTL only after a durable S3 archive receipt exists.
Retain detailed events/dedupe for 90 days from receipt. Accept event ingestion
only within 90 days of attempt allocation; after that return `attemptExpired`
and reconcile inventory via check-in. Preserve compact attempt/outcome metadata
and published release/deployment history permanently. Expiry is checked in code,
never delegated to asynchronous TTL.

## Deployment snapshots beyond transaction limits

### Stable membership cut

An eventually consistent customer GSI cannot supply a complete activation
snapshot. Maintain the base-table roster above synchronously with assignments.
Customer membership is immutable in v1. Snapshot membership includes all matching
assigned computers (including offline/revoked ones); live enabled state is always
a separate eligibility check. Pins do not remove membership, but pin-derived
offers/outcomes are shown separately. This makes the denominator explicit.

For v1 choose a short **per application/channel assignment-membership fence**
instead of a global revision service or an unbounded transaction. Assignment
entry/removal/channel moves check old and new gates in their write transaction;
therefore each such mutation either precedes the fence or is rejected with
retryable `snapshotInProgress`. Pin edits, check-ins, revocation, telemetry and
reads continue. Enrollment also continues but grants no assignments. The fence
is an availability tradeoff: membership edits can be blocked for up to 15 minutes
while a large activation builds. It does not weaken immediate revocation.

1. An operator first requests a bounded asynchronous preview. It is advisory
   (membership can change); report target/admitted counts, as-of time and selected
   release. The operator sends those reviewed counts with activation.
2. Activation transaction checks draft/revision, published release, vacant channel
   active slot, free roster gate and permission; reserves the slot, sets a fence
   and creates generation `g`, initial percentage, immutable customer list,
   membership cut timestamp and worker operation. Deployment stays `draft` with
   `activationState=building`; no target is eligible yet. Return 202.
3. A renewable 60-second worker lease with a monotonic fencing token protects
   checkpoints. The fixed manifest is customer IDs sorted lexically × 32 roster
   shards. Workers strongly Query each partition under the fence, at most 50
   targets/page. Each page transaction writes ≤50 deterministic target rows,
   updates its checkpoint/count, inserts a page receipt and checks generation,
   gate and lease ownership (comfortably below 100 actions/4 MiB). A page digest
   and conditional checkpoint ordinal prevent duplicate counting. An unknown
   commit outcome is resolved by reading the receipt/checkpoint before replay.
   In the same transaction update at most 32 histogram groups (bucket/313,
   capped at 31); with 50 target writes, checkpoint, receipt and control checks
   the transaction still has fewer than 100 items. This seals exact admitted
   counts for later percentage previews without rereading the fleet.
4. Target rows store membership, customer, assignment ID and hash version/bucket.
   Bucket v1 is the first unsigned 64 bits (big endian) of
   `SHA256(UTF8("relay-rollout-v1\n" + deploymentId + "\n" + computerId)) mod 10000`.
   Admit if `bucket < percentageBasisPoints`; 0 admits none, 10000 admits all.
   The hash is independent of activation generation, pause and promotion.
5. Reduce finished partition checkpoints into a fixed completion tree: each
   parent transaction checks at most 64 immutable completed children and writes
   one sealed count/digest record. At 3,200 partitions this needs at most 50 leaf
   groups and one root. Generation sealing verifies that root, the 32 histogram
   groups, totals and manifest digest, comfortably under 100 actions. Workers
   cannot mutate a sealed partition/generation; each step checks its parent
   state/fencing token. Never attempt 100 child checks plus a parent write.
6. If final target/admitted counts differ from the operator's reviewed counts,
   or the exact target count is zero,
   stop in `awaitingConfirmation`, expose exact counts and require an explicit
   revision-checked confirm action. Otherwise finalize directly. Final transaction
   checks sealed completion, live fence/slot, expected draft revision, same
   published release, commits generation pointer/state active/revision, clears
   fence, updates channel policy revision and writes audit/completion receipt.
   Only that pointer makes the snapshot visible to eligibility and target APIs.

Snapshot jobs are limited to 100 selected customers and 100,000 targets. Exceeding
the limit aborts the generation with an explicit error, not a truncated snapshot.
Preview uses the same keyed roster traversal without a fence and is labeled
approximate; no preview count grants authorization. Activation has a hard
15-minute deadline including any confirmation; a later retry obtains a new cut.

### Partial failure and recovery

A crashed worker resumes deterministic pages from committed checkpoints after
acquiring a higher lease token. Expired leases do **not** automatically release
the membership fence: a recovery worker either resumes within the deadline or
atomically marks the generation aborted, clears its owned gate/slot, increments
deployment revision and audits failure. An authorized cancel-activation action
does the same. Old workers condition on generation and fencing token, so they
cannot write/finalize after abort. Do not use TTL to release a lock.

An assignment mutation encountering a fence past its hard deadline invokes this
same conditional abort before retrying, so an unavailable background worker
cannot leave membership edits blocked indefinitely. Lease expiration alone
cannot clear a live build; the hard deadline or explicit cancellation can.

Aborted generations are never reusable or visible; a new activation uses a new
generation and cut. Garbage collection queries their known target/build shards
and deletes bounded pages after 24 hours. Failure during deletion is retryable.
The slot remains occupied through building/confirmation/active/paused, and is
released on build abort or completed/cancelled lifecycle. A release withdrawn
during build prevents finalization and triggers abort. Current-policy changes
remain independent of building but never alter fence/slot ownership.

### Outcomes and summary rebuild

Targets begin pending. A deployment offer/attempt/event transaction updates its
target's latest relevant offer/attempt projection with a monotonic projection
version. This per-target record plus immutable attempts/events is authoritative
evidence; counts are views. Pins/current do not count as deployment offers.

Streams workers update a contribution record and sharded counters transactionally
only if the incoming projection version is newer. Re-delivery/out-of-order
Streams records have no duplicate effect. Counters expose generation, as-of time
and lag; they cannot authorize promotion or an offer. Eligibility and stale counts
also depend on current policy/time: a bounded periodic reconciliation over known
target shards recomputes them; never label them transactionally exact.

Rebuild into a new summary generation: strongly traverse the committed target
shards, keyed-read attempt/progress/inventory and policy as needed, and condition
each contribution on the target version still matching the read. Capture/replay
Streams changes during traversal, allowing each newer contribution to replace
its predecessor. Checkpoint each shard; if lag exceeds Streams' 24-hour retention,
restart the rebuild rather than claim completeness. Swap the summary pointer
only after all shard checkpoints are complete and the consumer has caught up to
the recorded stream watermarks. Expose the rebuild interval/lag because live
policy and time can still change; this is not a global point-in-time count.
Keep the prior generation until cursors expire. If target projections themselves
need repair, query the base-table computer/month attempt directory from the
deployment's creation month through the repair cut, then keyed-read attempt
metadata, select deployment-source bindings and rebuild each target before
rebuilding counts. Checkpoint month/target traversal as a job; the GSI history
listing is not proof that all attempts have been enumerated.

Denominators are separate: snapshot targets, currently eligible, offered,
succeeded, failed, pending and stale. Outcome counts use each target's latest
relevant attempt, not event totals. Stale can overlap failure/pending; never sum
overlapping categories as a completion total. Offline/unknown never means success.

## Consequences and validation gates

This design buys keyed authorization and recoverable bulk work at the cost of
transactional roster write amplification, two GSIs, eventual operator views and
a temporary membership-write fence. The channel roster gate can become a write
hotspot; measure the 10,000-computer/ten-assignment planning envelope before
production. Raising limits or removing the fence requires a new ADR (for example,
an immutable versioned membership ledger), not an eventually consistent shortcut.

Reject alternatives for v1: GSI-based authorization/uniqueness, a single giant
target transaction/item, mutable published S3 keys, fleet-wide secrets, a global
policy counter incremented on every poll, and automatic release/current promotion.

The following are required tests in the owning implementation slices, not tests
added by this documentation change:

| Scenario | Evidence required |
| --- | --- |
| Revoke/pause/withdraw versus offer/refresh/attempt | Force each transaction ordering; only pre-change authorizations succeed, no mixed revisions or fallback. |
| Current, assignment and deployment edit races | Conditional conflicts/retries preserve one consistent dependency vector; remove/recreate does not reuse revisions. |
| Duplicate SemVer/activation | Concurrent transactions yield one version reservation/active slot; build metadata duplicates fail. |
| Snapshot >100 items | Crash before/after page commit, lease takeover, new assignment attempts, confirmation mismatch, deadline, withdrawal and abort; exact membership, no partial visibility/double counts. |
| Pagination | Empty shards, DynamoDB 1 MiB continuation, tampered/expired/query-changed cursors, all committed target shards exactly once. |
| Enrollment and retries | Lost secret response/recovery, expiry, concurrent token use, request-ID-only attack; one identity and no plaintext secret persistence. |
| Attempts/events/inventory | Duplicate keys/sequences, conflicting IDs, 42 success before 41 failure, late progress/reboot/terminal conflicts, lower inventory revision. |
| Projection rebuild | Duplicate/out-of-order Streams records, concurrent writes, lag beyond retention, interrupted rebuild; no inflated counts or authorization from counters. |
| Artifact publish | Incomplete verification, changed draft/upload generation, immutable version references, signer/size/digest failure; no publish on failure. |

Validate real DynamoDB transaction cancellation, index lag, pagination and Streams
behavior in the later integration-test slices; in-memory mocks cannot establish
these guarantees. AWS integration tests require an isolated table/bucket/pool and
least-privilege test credentials. No live AWS provisioning is part of issue #3.

AWS constraints: [transactions](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/transaction-apis.html),
[read consistency](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.ReadConsistency.html),
[Query pagination](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Query.Pagination.html),
[Streams retention](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Streams.html),
[S3 presigned URLs](https://docs.aws.amazon.com/AmazonS3/latest/userguide/using-presigned-url.html).
