# Relay v1 HTTP contract

Status: decided for implementation by [issue #3](https://github.com/jtron5050/relay/issues/3);
these routes are **not yet implemented**. The only existing routes are the two
public `GET /health` endpoints. This contract refines [product spec §§3–6](product-spec.md)
and uses [ADR 0001](adr/0001-v1-storage-and-consistency.md) for storage,
transactions, authorization, idempotency, snapshot jobs and cursor semantics.
Later feature issues implement and test these contracts incrementally.

## Common wire conventions

- Separate HTTPS origins for Admin and Agent APIs; all new routes start `/v1`.
  JSON uses camelCase and string enums. IDs are opaque, case-sensitive strings
  (1–128 ASCII characters); UTC timestamps use RFC 3339 with `Z`. Integers are
  JSON numbers except inventory revisions, event sequences and attempt numbers,
  which are decimal strings to avoid loss above JavaScript's safe integer range.
- JSON DTO fields below are required unless marked `?` (optional); `T|null`
  explicitly allows null. Reject unknown request fields and enum values with 400
  to catch misspelled safety controls. Response additions must be tolerated by
  clients. Do not accept storage keys, staff roles or authoritative attempt
  numbers from request bodies.
- Resource reads/mutation responses include an integer `revision` and an ETag
  of the form `"rev-7"`. Mutable resource operations/actions require `If-Match`
  with that ETag. First assignment creation uses `If-None-Match: *`; later
  recreation uses the tombstone revision. Missing preconditions return 428
  `revisionRequired`; stale revisions return 409 `revisionConflict`, with the
  current revision if the caller may read it. No wildcard If-Match overwrites.
- Every Admin POST/PUT/DELETE requires `Idempotency-Key` (random UUID recommended,
  16–128 printable ASCII characters). Agent attempt creation and rotation also
  require it; enrollment uses its persistent request ID, check-ins use inventory
  revision and installation events use event ID/sequence. Same key/body replays
  the result; mismatched reuse is 409. ADR 0001 defines scope, expiry and secret
  exceptions. Responses containing secrets or signed URLs use `Cache-Control:
  no-store`; never put these values in logs or example repositories.
- Creation returns 201 + `Location`; synchronous updates/actions return 200 with
  the revised resource; removal returns 204 with the tombstone ETag. Asynchronous
  work returns 202 + `Location: /v1/operations/{id}` and an Operation DTO. Agent
  duplicate event/check-in acknowledgments use 200; attempt replay preserves 201
  and its original attempt ID/number. No successful async response claims the
  work is complete.
- Collection queries accept `limit?` and `cursor?` and return
  `Page<T> = {items:T[], nextCursor:string|null}`. All additional supported filters
  are explicitly listed below; reject others. Lists are shard-major views, not
  globally ordered or point-in-time snapshots. Details and authorization are
  authoritative. A deployment list requires application scope; there is no
  unbounded cross-application list or arbitrary search endpoint.
- Errors have `{code, message, correlationId, retryable, details?}`; messages are
  safe for callers. Accept/return `X-Correlation-Id` (UUID; generate if absent or
  invalid). Never include stack traces, token hashes, signed URLs or another
  computer's IDs in errors. 401 covers invalid/expired credentials; 403 covers a
  known staff permission denial; use 404 for nonexistent or inaccessible objects.
  400 covers invalid input; 409 state/idempotency/revision conflicts; 410 expired
  offers/attempts/cursors; 413 oversized bodies; 415 wrong content type; 429
  throttling; 503 transient dependencies or exhausted policy retries. Retryable
  409/429/503 responses include `Retry-After` seconds (default 5).

## Permission matrix

All Admin routes require a Cognito access token and the exact operation permission.
Roles are additive allowlists; absent/unknown roles grant nothing. Staff operate
the internal fleet globally in v1; customer IDs do not represent customer login
scopes. Future tenant-scoped staff access requires a new permission model.

| Permission / operations | Administrator | Release operator | Support viewer | Agent |
| --- | --- | --- | --- | --- |
| `fleet.read`, `catalog.read`, `deployments.read`, `history.read`, `audit.read`, operation status | Yes | Yes | Yes | No |
| `customers.write`, `applications.write` (including channel creation/update) | Yes | No | No | No |
| `enrollment.write`, `computers.revoke`, `credentials.rotate` | Yes | No | No | No |
| `releases.write`, `releases.publish`, `releases.withdraw`, `channels.current.write` | Yes | Yes | No | No |
| `assignments.write`, `pins.write`, `deployments.write` | Yes | Yes | No | No |
| `rollback.authorize` (pin, current selection or deployment) | Yes | Yes | No | No |
| Enroll/recover with customer-bound bootstrap secret | No role bypass | No | No | Bootstrap proof only |
| Check-in, decisions, own offer refresh, own attempts/events, self rotation | No staff-token bypass | No | No | Enabled computer only |
| Public `/health` | Public | Public | Public | Public |

`rollback.authorize` is checked in addition to the write permission whenever
`allowDowngrade=true`; require a nonempty safe `rollbackReason`. Persist actor,
reason and selecting policy revision; the agent must still advertise downgrade
capability. Support reads never reveal bootstrap secrets, credentials, signed
upload/download URLs or hash lookup keys. All Agent references must bind to the
authenticated computer, even if the caller knows a valid offer/attempt ID.

## Admin routes

Within this table `a`, `ch`, `r`, `pc` and `d` stand for application, channel,
release, computer and deployment IDs. These are path placeholders, not literals.
`U` means revision precondition required; `C` creates a new resource; every
mutation has the idempotency requirement above. GETs have no mutation headers.

| Method and route | Request/query → response | Permission / concurrency |
| --- | --- | --- |
| GET `/v1/customers` | pagination → Page<Customer> | fleet.read |
| POST `/v1/customers` | CreateCustomer → Customer | customers.write; C |
| GET, PUT `/v1/customers/{id}` | GET → Customer; PUT EditCustomer → Customer | fleet.read / customers.write; U for PUT |
| GET `/v1/customers/{id}/computers` | pagination, `state?=enabled\|revoked`, `lastSeenBefore?` (only with state) → Page<Computer> | fleet.read; GSI view |
| GET `/v1/computers/{pc}` | → Computer (authoritative) | fleet.read |
| POST `/v1/computers/{pc}/revoke` | Reason → Computer | computers.revoke; U; irreversible v1 |
| POST `/v1/computers/{pc}/credential-rotations` | Empty → CredentialResult | credentials.rotate; U; one-time secret |
| POST `/v1/enrollment-tokens` | CreateEnrollmentToken → EnrollmentTokenResult | enrollment.write; C; one-time secret |
| POST `/v1/enrollment-tokens/{id}/revoke` | Reason → EnrollmentTokenMetadata | enrollment.write; U |
| GET `/v1/enrollment-tokens/{id}` | → EnrollmentTokenMetadata | enrollment.write; no secret |
| GET, POST `/v1/applications` | GET pagination → Page<Application>; POST CreateApplication → Application | catalog.read / applications.write; C for POST |
| GET, PUT `/v1/applications/{a}` | GET → Application; PUT EditApplication → Application | catalog.read / applications.write; U for PUT |
| GET, POST `/v1/applications/{a}/channels` | GET pagination → Page<Channel>; POST CreateChannel → Channel | catalog.read / applications.write; C for POST |
| GET, PUT `/v1/applications/{a}/channels/{ch}` | GET → Channel; PUT EditChannel → Channel | catalog.read / applications.write; U for PUT |
| PUT `/v1/applications/{a}/channels/{ch}/current` | SetCurrent → Channel | channels.current.write; U on channel |
| GET `/v1/applications/{a}/channels/{ch}/current-history` | pagination, `from`, `to` → Page<CurrentChange> | history.read |
| GET, POST `/v1/applications/{a}/releases` | GET pagination → Page<Release>; POST CreateRelease → Release | catalog.read / releases.write; C for POST |
| GET, PUT `/v1/applications/{a}/releases/{r}` | GET → Release; PUT EditRelease → Release | catalog.read / releases.write; U for PUT; draft only |
| POST `/v1/applications/{a}/releases/{r}/uploads` | RequestUpload → UploadResult | releases.write; U on release; draft only |
| POST `/v1/applications/{a}/releases/{r}/uploads/{uploadId}/complete` | CompleteUpload → Release | releases.write; U on release; records exact uploaded version |
| POST `/v1/applications/{a}/releases/{r}/package-verifications` | VerifyPackage → Operation | releases.write; U on release; 202 |
| POST `/v1/applications/{a}/releases/{r}/publish` | Empty → Release | releases.publish; U |
| POST `/v1/applications/{a}/releases/{r}/withdraw` | Reason → Release | releases.withdraw; U |
| GET `/v1/computers/{pc}/assignments` | pagination → Page<Assignment> (including tombstones) | fleet.read |
| GET `/v1/computers/{pc}/assignments/{a}` | → Assignment (including tombstone and ETag) | fleet.read |
| PUT `/v1/computers/{pc}/assignments/{a}` | SetAssignment → Assignment | assignments.write; first create precondition or U |
| DELETE `/v1/computers/{pc}/assignments/{a}` | no body → 204 | assignments.write; U |
| PUT `/v1/computers/{pc}/assignments/{a}/pin` | SetPin → Assignment | pins.write; U on assignment |
| DELETE `/v1/computers/{pc}/assignments/{a}/pin` | no body → Assignment | pins.write; U on assignment |
| GET, POST `/v1/applications/{a}/deployments` | GET pagination → Page<Deployment>; POST CreateDeployment → Deployment | deployments.read / deployments.write; C for POST |
| GET, PUT `/v1/deployments/{d}` | GET → Deployment; PUT EditDeployment → Deployment | deployments.read / deployments.write; U for PUT; draft/not building only |
| POST `/v1/deployments/{d}/previews` | PreviewDeployment → Operation | deployments.write; U; 202 |
| POST `/v1/deployments/{d}/activate` | ActivateDeployment → Operation | deployments.write; U; 202 |
| POST `/v1/deployments/{d}/confirm-activation` | ConfirmActivation → Operation | deployments.write; U; 202 |
| POST `/v1/deployments/{d}/cancel-activation` | Reason → Deployment | deployments.write; U; releases build fence/slot |
| POST `/v1/deployments/{d}/promote` | PromoteDeployment → Deployment | deployments.write; U |
| POST `/v1/deployments/{d}/{action}` | Reason → Deployment; action is pause/resume/complete/cancel only | deployments.write; U |
| GET `/v1/deployments/{d}/targets` | pagination, `outcome?` → Page<DeploymentTarget> | deployments.read; committed generation only |
| GET `/v1/deployments/{d}/summary` | → DeploymentSummary | deployments.read; eventual with as-of/lag |
| GET `/v1/computers/{pc}/installation-attempts` | pagination, `from`, `to` → Page<Attempt> | history.read |
| GET `/v1/computers/{pc}/installation-attempts/{id}` | → Attempt | history.read; binding checked |
| GET `/v1/computers/{pc}/installation-attempts/{id}/events` | pagination → Page<InstallationEvent> | history.read |
| GET `/v1/audit-events` | pagination, `from`, `to`, optional pair `targetType/targetId` → Page<AuditEvent> | audit.read; no arbitrary actor filter |
| GET `/v1/operations/{id}` | → Operation | original operation's read permission; credential work additionally requires enrollment.write/credentials.rotate |

`Empty` is `{}`. No generic PATCH, computer-customer moves, hard deletes, release
version edits, re-enabling revoked identities, or arbitrary deployment actions
are defined in v1. SetAssignment cannot set a pin or rollback intent; change pin
through its privileged route. Moving channels preserves an existing pin and its
rollback intent until an explicit pin removal. Pin removal restores
normal deployment/current selection and never changes current or a deployment.

Lifecycle preconditions: activate only a draft with no live build; pause only
active; resume only paused; promote only active; complete/cancel only active or
paused. Cancel-activation applies only to building/awaitingConfirmation and leaves
the deployment draft with activationState=failed. Completed/cancelled are terminal;
all invalid transitions return 409 `invalidState`. An activation with zero targets
is allowed only after explicit confirmation of its exact zero counts.

### Admin request DTOs

These are compact field definitions; `Reason={reason:string}` is required for
destructive/lifecycle actions, with a safe human explanation. Names are display
labels, not unique identities. All `allowDowngrade` fields default to false when
omitted; setting false clears earlier rollback intent for that policy revision.

| DTO | Fields / constraints |
| --- | --- |
| CreateCustomer / EditCustomer | `{name}` |
| CreateApplication / EditApplication | `{name, updatePolicy:{platform:"windows", architectures:("x64"\|"arm64")[]}}`; nonempty distinct architecture set |
| CreateChannel / EditChannel | `{name}`; initial current is null, cannot be changed by this DTO |
| CreateEnrollmentToken | `{customerId, expiresInSeconds?}` |
| CreateRelease | `{productVersion, notes?}`; valid SemVer 2.0; uniqueness excludes build metadata; version cannot later change |
| EditRelease | `{notes}`; manifests updated only by upload/verification flow |
| RequestUpload | `{architecture, sizeBytes, sha256, fileVersion, signer:Signer, installerHints:InstallerHints, uploadId?, parts?:{first:int,count:int}}`; SHA-256 is 64 lowercase hex; size positive; file version has four unsigned 16-bit parts; uploadId/parts only for existing-session URL requests |
| CompleteUpload | `{parts:{number:int,etag:string}[]}`; empty for a single PUT; ≤2,048 ordered distinct parts, ETags ≤128 ASCII characters |
| VerifyPackage | `{uploadId}`; server resolves expected object version from completed upload, never trusts a caller-supplied bucket/key |
| SetCurrent | `{releaseId:string\|null, allowDowngrade?, rollbackReason?}`; null explicitly clears; rollback true with null is invalid |
| SetAssignment | `{channelId}`; application comes from route, computer/customer from authoritative identity |
| SetPin | `{releaseId, allowDowngrade?, rollbackReason?}`; published same-application release required |
| CreateDeployment / EditDeployment | `{channelId, releaseId, customerIds:string[], allowDowngrade?, rollbackReason?}`; application fixed by create route; after activation all these fields freeze |
| PreviewDeployment | `{percentageBasisPoints}` |
| ActivateDeployment | `{percentageBasisPoints, previewOperationId, expectedTargetCount, expectedAdmittedCount}`; preview must bind to same draft revision/release/customer set/percentage and be ≤15 minutes old |
| ConfirmActivation | `{generationId, expectedTargetCount, expectedAdmittedCount}`; acknowledge exact completed generation counts |
| PromoteDeployment | `{percentageBasisPoints, expectedAdmittedCount}`; strictly higher than current; return 409 `previewChanged` with exact count if expectation differs |
| Signer | `{publisher, certificateSha256}`; bounded metadata, not the external agent's publisher trust policy |
| InstallerHints | `{format:"msi"\|"exe"\|"msix", silent:bool, rebootBehavior:"agentManaged"}`; no shell scripts, command strings or executable server instructions |

Releases support at most one package per architecture; replacing a draft upload
invalidates its earlier verification. Publication requires all declared variants
verified with size/digest/signer metadata. A missing architecture is a decision
reason, not an invitation to choose a different release. Verification jobs stream
S3 bytes and compute their digest; they do not execute installers.

Promotion's exact admitted count comes from the frozen bucket histogram sealed
with the snapshot (10,000 buckets stored in bounded shard records, not one large
item). Preview responses include that histogram's cumulative admitted count for
the requested percentage; active-deployment previews use the frozen histogram,
not a new roster. For an inactive draft they traverse the advisory live roster.

### Admin response DTOs

`Resource={id, revision, createdAt, updatedAt}` is included on mutable resources.
All optional references below are returned as explicit null when absent. A detail
response may include more fields than its list projection, but never secrets.

| DTO | Additional fields |
| --- | --- |
| Customer / Application | name; Application also updatePolicy |
| Channel | applicationId, name, currentReleaseId:null\|string, allowDowngrade, rollbackReason:null\|string, activeDeploymentId:null\|string, policyRevision |
| Computer | customerId, hostname, platform, architecture, state:enabled\|revoked, lastSeenAt:null\|timestamp, stale:bool, inventoryRevision:decimalString, inventory:InventoryEntry[]; no credential hashes |
| Assignment | computerId, applicationId, channelId:null\|string, removed:bool, pin:null\|{releaseId, allowDowngrade, rollbackReason, actorId, changedAt} |
| Release | applicationId, productVersion, notes, state:draft\|published\|withdrawn, packages:PackageManifest[] |
| PackageManifest | packageId, architecture, fileVersion, sizeBytes, sha256, signer, installerHints, verificationState:pending\|verified\|failed; no storage write capability |
| UploadResult | uploadId, releaseRevision, mode:single\|multipart, uploadUrl:null\|string, expiresAt, requiredHeaders, partSizeBytes:null\|integer, parts:{number,uploadUrl,requiredHeaders}[]; fresh URLs require renewed authorization |
| EnrollmentTokenMetadata | id, revision, customerId, expiresAt, state:available\|consumed\|revoked, consumedAt:null\|timestamp |
| EnrollmentTokenResult | metadata:EnrollmentTokenMetadata, token (one-time bootstrap secret) |
| CredentialResult | computerId, computerRevision, credential, expiresAt:null\|timestamp, previousCredentialValidUntil:null\|timestamp |
| CurrentChange | id, applicationId, channelId, previousReleaseId:null\|string, releaseId:null\|string, allowDowngrade, rollbackReason:null\|string, actorId, changedAt, channelRevision |
| Deployment | applicationId, channelId, releaseId, customerIds, allowDowngrade, rollbackReason:null\|string, state:draft\|active\|paused\|completed\|cancelled, activationState:none\|building\|awaitingConfirmation\|failed\|committed, generationId:null\|string, percentageBasisPoints, snapshotTargetCount:null\|integer, admittedCount:null\|integer |
| DeploymentTarget | computerId, customerId, bucket, currentlyEligible:bool, eligibilityReason:null\|string, latestOfferId:null\|string, latestAttemptId:null\|string, outcome, stale:bool, asOf |
| DeploymentSummary | deploymentId, generationId, snapshotTargets, admitted, currentlyEligible, offered, succeeded, failed, pending, stale, pinSelected, asOf, lagSeconds, rebuilding:bool; overlapping denominators explicitly labeled |
| Operation | id, kind, resourceId, state:pending\|running\|awaitingConfirmation\|succeeded\|failed, createdAt, updatedAt, deadlineAt, result:null\|VerificationResult\|PreviewResult\|ActivationResult, error:null\|Error |
| VerificationResult | releaseId, releaseRevision, packageId, verificationState |
| PreviewResult | deploymentId, draftRevision, releaseId, percentageBasisPoints, targetCount, admittedCount, asOf, exact:bool; exact only for committed snapshot previews |
| ActivationResult | deploymentId, deploymentRevision, generationId, targetCount, admittedCount |
| AuditEvent | id, actorKind, actorId, action, targetType, targetId, occurredAt, correlationId, beforeRevision:null\|integer, afterRevision:null\|integer, summary |

Deployment target views with live eligibility enrichment use the same bounded
per-computer evaluation reads as update checks, but never issue an offer. Keep
list pages within the work budget; return a cursor for remaining enrichment.
Summary `pending` means no terminal relevant attempt; it can overlap offered,
in-progress and stale. Succeeded/failed select the latest relevant attempt;
`pinSelected` is a separate live eligibility view and never credited as deployment
success. Display denominator names rather than summing all fields.

## Agent routes and DTOs

Use `Authorization: Bearer <per-computer-secret>` for every route except enrollment
and recovery, which prove the bootstrap secret in the JSON body over HTTPS.
Enrollment request IDs, event IDs and inventory revisions must survive agent
restarts. There is no caller-selectable computer/customer ID in these requests.

| Method and route | Request → response | Result/authorization |
| --- | --- | --- |
| POST `/v1/enroll` | Enroll → EnrollmentResult | 201, bootstrap proof; identical request cannot allocate another computer |
| POST `/v1/enrollment-recoveries` | RecoverEnrollment → EnrollmentResult | 200, original bootstrap proof plus request binding within recovery window; rotates secret |
| POST `/v1/check-ins` | CheckIn → CheckInResult | 200, authenticated computer; full ordered inventory snapshot |
| POST `/v1/update-checks` | UpdateCheck → UpdateCheckResult | 200, authenticated computer; one decision per requested/live assigned application |
| POST `/v1/offers/{id}/download-refreshes` | Empty → DownloadResult | 200; fresh authorization of same offer/source; no new attempt |
| POST `/v1/installation-attempts` | CreateAttempt → Attempt | 201; persistent Idempotency-Key; fresh offer/policy authorization |
| POST `/v1/installation-events` | EventBatch → EventBatchResult | 200 with per-event acknowledgments; authenticated bindings; no new policy eligibility required for historical telemetry |
| POST `/v1/credential-rotations` | Empty → CredentialResult | 200; persistent Idempotency-Key; current enabled identity; old-generation overlap bounded |

| Request DTO | Fields / semantics |
| --- | --- |
| Enroll | `{token, enrollmentRequestId:UUID, hostname, platform:"windows", architecture:"x64"\|"arm64"}`; retry binding includes identity fields |
| RecoverEnrollment | `{token, enrollmentRequestId:UUID}`; cannot change original identity fields |
| InventoryEntry | `{applicationId, installationState:"absent"\|"installed", installedProductVersion:string\|null, installedFileVersion:string\|null, installedReleaseId:string\|null}`; absent requires all three version/reference fields null; installed may be unknown |
| CheckIn | `{inventoryRevision:decimalString, hostname, platform, architecture, applications:InventoryEntry[]}`; full inventory snapshot, not patch; omitted applications become unknown, never inferred absent |
| UpdateCheck | `{platform, architecture, capabilities:{downgrade:bool}, applications:InventoryEntry[]}`; reported inventory bound to any issued offer; does not overwrite stored check-in inventory |
| CreateAttempt | `{offerId}`; server binds computer/application/release/package/source and assigns number; no caller-selected attempt number |
| InstallationEvent | `{eventId:UUID, attemptId, applicationId, releaseId, offerId, sequence:decimalString, observedAt, state, diagnosticCode:string\|null, diagnosticMessage:string\|null}` |
| EventBatch | `{events:InstallationEvent[]}`; nonempty, each event is atomic independently; not an all-or-nothing batch |

CheckIn rejects duplicate application IDs and same revision/different content.
An older revision returns an acknowledgment with `inventoryStatus=stale`, without
regressing inventory. Hash hostname/platform/architecture and full inventory in
the revision payload; require agents to increment it when any changes. Server
receipt time updates last-seen independently of device timestamps. The API does
not translate an attempt success into installed inventory automatically.

Update checks enumerate authoritative live assignments (at most ten) and union
them with requested applications (at most ten total). Omitted assigned application
inventory is unknown and produces `installedVersionUnknown`; it is never inferred
absent. Unassigned requested applications return `noApplicableRelease/unassigned`
without disclosing catalog/release details. Each application is evaluated under
its own consistent revision vector; the batch does not promise one cross-app
transaction. If a dependency fails, return that application's retryable error
with no offer; do not fabricate noApplicableRelease.

| Response DTO | Fields |
| --- | --- |
| EnrollmentResult | computerId, customerId, credential (one-time), credentialExpiresAt:null\|timestamp, nextCheckAfterSeconds |
| CheckInResult | acceptedInventoryRevision:decimalString, inventoryStatus:applied\|duplicate\|stale, serverTime, nextCheckAfterSeconds |
| UpdateCheckResult | decisions:ApplicationDecision[], serverTime |
| ApplicationDecision | applicationId, decision, reason, selection:null\|Selection, desiredRelease:null\|{releaseId, productVersion}, offer:null\|Offer, error:null\|Error, nextCheckAfterSeconds |
| Selection | source:pin\|deployment\|channelCurrent, sourceId, policyRevision:string; pin also pinRevision, deployment also deploymentId/generationId/percentageBasisPoints, current also channelId/channelRevision |
| Offer | offerId, releaseId, productVersion, policyRevision:string, expiresAt, package:DownloadPackage |
| DownloadPackage | packageId, architecture, fileVersion, sizeBytes, sha256, signer, installerHints, downloadUrl, downloadExpiresAt |
| DownloadResult | offerId, policyRevision:string, package:DownloadPackage |
| Attempt | id, computerId, applicationId, releaseId, offerId, packageId, source:Selection, attemptNumber:decimalString, state, latestSequence:decimalString, createdAt, ingestionExpiresAt |
| EventBatchResult | results:{eventId, status:accepted\|duplicate\|rejected, error:null\|Error}[]; one result per input, original order |

An attempt starts in `offered` with sequence `"0"`; first reported sequence is
at least `"1"`. Events use states offered, downloading, installing,
rebootRequired, succeeded, failed, with ordering/transition rules in ADR 0001.
Clients retry only unacknowledged/retryable entries using identical IDs/payloads;
an API-level 401/413/429 rejects the whole request before per-event processing.
Telemetry for an authorized attempt remains valid after pause/withdrawal (subject
to authenticated enabled computer and ingestion window). Never rerun current
policy to reject such history. Current inventory and latest attempt remain
separate response concepts.

### Decision and reason vocabulary

`decision` describes the actionability, `selection.source` describes provenance,
and `reason` supplies the machine-readable explanation. `desiredRelease` means
the selected exact release, not last offered or last reported installed release.
`offer` is present **only** for updateAvailable. A selected failure never falls
back to another source. Error decisions have no desired release or offer.

| decision | Allowed reasons | Offer / interpretation |
| --- | --- | --- |
| updateAvailable | freshInstall, upgrade, rollbackAuthorized | Offer after all checks; pin/deployment/current provenance is explicit. |
| upToDate | productVersionEqual | No offer; file-version difference alone does not trigger replacement. |
| noApplicableRelease | unassigned, channelCurrentUnset | No offer, no uninstall intent. Paused/completed/cancelled or non-admitted deployment falls through to current, which may be unset. |
| releaseWithdrawn | selectedReleaseWithdrawn | No offer/fallback, including pins/current. |
| incompatible | platformUnsupported, architectureUnsupported, installedVersionUnknown, rollbackNotAuthorized, downgradeUnsupported | No offer/fallback; rollbackNotAuthorized checked before downgradeUnsupported. |
| error | policyChanged, dependencyUnavailable | Per-application retryable error, no offer; honor retry guidance. |

`pinned` is represented as `selection.source=pin`, **not a mutually exclusive
decision**. This resolves the blueprint's illustrative `pinned` label: an eligible
pin with a differing version returns updateAvailable, an equal version returns
upToDate, and a withdrawn pin returns releaseWithdrawn, all with pin provenance.
An explicit removal restores deployment/current selection.

Check order: authenticated enabled identity → live assignment/application policy
→ source selection (pin > admitted active deployment > current) → publication
state → platform/architecture → installed presence/version → SemVer precedence
→ selecting-policy rollback authorization → agent downgrade capability. A
missing/inconsistent referenced resource is dependencyUnavailable, not a quiet
fall-through. A pin to a withdrawn release remains selected. Never compare file
version for ordering or use an unrecognized Relay release ID to reject otherwise
valid installed SemVer. Build metadata is ignored for precedence/uniqueness.

Example (illustrative IDs, digest and token; no actual URL/credential):

```json
{
  "applicationId": "app_123",
  "decision": "updateAvailable",
  "reason": "upgrade",
  "selection": {
    "source": "pin",
    "sourceId": "assignment_123",
    "pinRevision": 7,
    "policyRevision": "pv1_opaque"
  },
  "desiredRelease": {"releaseId": "rel_101", "productVersion": "2.4.1"},
  "offer": {
    "offerId": "offer_456",
    "releaseId": "rel_101",
    "productVersion": "2.4.1",
    "policyRevision": "pv1_opaque",
    "expiresAt": "2026-10-30T20:00:00Z",
    "package": {
      "packageId": "pkg_101_x64",
      "architecture": "x64",
      "fileVersion": "2.4.1.124",
      "sizeBytes": 52428800,
      "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "signer": {"publisher": "Example publisher", "certificateSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"},
      "installerHints": {"format": "msi", "silent": true, "rebootBehavior": "agentManaged"},
      "downloadUrl": "https://example.invalid/short-lived-download",
      "downloadExpiresAt": "2026-10-30T19:10:00Z"
    }
  },
  "error": null,
  "nextCheckAfterSeconds": 900
}
```

## Resource limits and defaults

These are explicit v1 implementation defaults, not measured capacity guarantees.
Configuration can tighten them; raising transaction/body/shard/snapshot limits
requires budget review and compatible documentation. Reject excess work before
partial mutation; scheduled jobs report limit failures explicitly.

| Resource | Default / hard v1 bound |
| --- | --- |
| JSON request/response body | Request ≤256 KiB; response ≤1 MiB; artifacts transfer directly to S3, not API Gateway |
| Name / hostname / notes / reason | 200 / 255 / 8,192 / 1,024 Unicode characters; diagnostic code 64, message 2,048; reject control characters except notes newlines |
| Versions / signer | Product version ≤128 ASCII characters (including agent-reported input); signer publisher ≤200 characters and certificateSha256 exactly 64 lowercase hex; stored SemVer keys exclude build metadata |
| IDs / idempotency key / cursor | IDs ≤128 ASCII; keys 16–128 printable ASCII; cursor ≤4 KiB |
| Page | 50 default, 100 maximum; ≤200 evaluated rows, ≤1 MiB DynamoDB pages, ≤4 partitions; may return an empty page with nextCursor |
| Audit, current history, attempt history | Require `from` inclusive, `to` exclusive; ≤31 UTC days per request; cursor binds range |
| Applications per computer/check | Ten assignments and ≤10 total application entries; one assignment per application |
| Inventory revision, event sequence, attempt number | Positive unsigned 64-bit decimal string (initial no-inventory/sequence is "0"); overflow is an explicit conflict, never wrap |
| Telemetry batch | 50 events and ≤256 KiB total; safe per-event results; no unbounded diagnostics |
| Release packages | One x64 and one ARM64 variant at most; each ≤10 GiB; upload uses bounded multipart S3 flow when needed |
| Artifact upload | Upload session ≤24 hours; each presigned request ≤600 seconds; multipart ≤2,048 parts, part size ≥8 MiB except final part; ≤50 part URLs per request |
| Credential / token entropy | 256 random bits; only hashes persisted |
| Enrollment | Token expires after 86,400 seconds by default; caller may request 300–86,400; single use; recovery ≤600 seconds after consumption |
| Credential rotation | Old generation overlap 300 seconds, at most two generations; revoked computer cannot rotate; new rotation invalidates older overlap |
| Offer / signed URL | Offer lifetime 3,600 seconds; upload/download URL ≤600 seconds; fresh authoritative authorization for refresh |
| Idempotency | Admin receipt 24 hours; attempts/enrollment bindings permanent; no secret/URL replay; cursor expiry 900 seconds |
| Deployment | Percentage integer 0–10,000 basis points; 1,000 = 10%; promote only upward; ≤100 distinct selected customers, ≤100,000 snapshot targets; 32 immutable target/roster shards |
| Snapshot/verification/preview jobs | Snapshot/preview deadline 900 seconds, lease 60 seconds; verification deadline 3,600 seconds; failed job never publishes partial content/membership |
| Poll/check-in | 900 seconds default, agent random jitter ±20%; stale after max(three expected intervals, 2,700 seconds) |
| Backoff | Start 5 seconds, exponential with jitter, cap 900; server Retry-After takes precedence |
| Rate limits | Agent identity: 60 requests/minute, burst 20; staff subject: 120/minute, burst 30; enroll/recovery: 10/minute per source IP and five/minute per token hash; 429 + Retry-After; never log hash values |
| Retention | Detailed telemetry/dedupe 90 days; attempt ingestion ≤90 days after allocation; audit ≥365 days then archived before TTL; compact history retained |

Upload flow: a new RequestUpload omits uploadId/parts and creates a fresh staging
session; existing-session URL requests include uploadId and the same expected
manifest, plus a part range for multipart (count ≤50). For multipart, uploadUrl
is null and parts contains signed part instructions; for single, uploadUrl is a
string and parts is empty. Use 8 MiB parts (at most 1,280 for a 10 GiB package).
CompleteUpload calls S3 CompleteMultipartUpload (or resolves the single PUT),
records the exact object version and never publishes automatically. Persist a
completing state first; on an unknown S3 result, recover by HEAD of the unique
staging key and upload metadata before completing the DynamoDB transaction.
Limit ETags so the completion body remains under 256 KiB. Verification requires
completion. Failed/mismatched uploads get new staging keys; cleanup aborts stale
multipart sessions. ETags select parts, never replace digest verification.

## Implementation handoff and acceptance

Issue #4 owns common safety/error/revision conventions; #5 persistence primitives;
#6–12 staff/storage/enrollment foundations; #13–23 distribution contracts;
#24–27 snapshots, promotion, pins and fleet views; #28–33 hosting and operational
validation. Implement only the selected slice and publish host-specific OpenAPI
from its DTOs when routes exist. Do not register placeholder protected routes
before authorization is enforced.

For each slice test permission denial and cross-computer bindings, revision and
idempotency races, body/work limits, explicit error/reason enums and secret-free
logs. ADR 0001 lists the storage/snapshot race scenarios. Preserve all product
spec acceptance cases, including standing channel current, independent pins,
withdrawal without fallback, delayed telemetry and manual rollback. Architecture,
throughput, availability and recovery targets require later integration/load/
restore evidence; this documentation does not claim those guarantees today.
