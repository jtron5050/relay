# Relay — Product specification

Status: proposed blueprint · Version: 1.0 · October 27, 2026

## 1. Purpose

Relay distributes your company's application updates to customer computers. Its promise is **the right release, on the right computers, with an accountable rollout history**. It connects publishing intent to observed installation outcomes without requiring operators to contact each customer machine.

Success means an operator can answer: What should this computer run? Why is it eligible? What was offered? What actually installed? What failed, and what can we do next?

Relay is internally operated. Customers are organizations containing computers, not independent publishers. The first product is an API-backed service; an administrative web application and possible customer experiences will follow.

### Scope and decisions

Confirmed requirements: separate Admin and Agent APIs; Windows computers; DynamoDB storage; Cognito authentication and authorization; opaque Windows packages; controlled rollouts with manual promotion. Customer self-service, the Windows agent implementation, and the web application are outside v1.

Architecture and numerical defaults below are recommendations for implementation, not existing capabilities. The repository currently contains .NET 10 API hosts with health endpoints, shared Core and Infrastructure libraries, and an empty CDK stack. Authentication, persistence, publishing, and deployment behavior remain to be built.

Non-goals include remote command execution, general endpoint management, application licensing, dependency orchestration, delta patching, installer authoring, and automatic health-based rollout advancement.

## 2. People and product workflows

| Persona | Responsibilities |
| --- | --- |
| Administrator | Manage staff access, customers, applications, enrollment, credentials, and all operator capabilities. |
| Release operator | Prepare and publish releases, manage assignments, run deployments, inspect outcomes, and perform explicit recovery. |
| Support viewer | Inspect fleet state, release history, and deployment outcomes without mutations or credential access. |

Cognito authenticates staff and supplies role/group claims. Relay enforces permissions for each operation; possession of a token alone grants no publishing authority.

### Publish and distribute

1. An operator creates an application and channels such as preview and stable.
2. The operator creates a release, uploads its packages through authorized upload URLs, and completes artifact verification.
3. Publication freezes the release and its package manifests. Publishing alone does not deploy it.
4. An operator creates a deployment for an application/channel and selected customers, reviews the target count and release, and activates an initial percentage.
5. Eligible computers discover the release through polling, download directly from storage, and report installation progress.
6. The operator reviews success, failure, pending, and stale counts before increasing the percentage or pausing.

### Enroll and assign

An administrator creates an expiring enrollment token bound to a customer. The separately developed agent exchanges it for a computer identity and credential. Operators assign applications and channels to the computer; enrollment alone does not grant access to every application.

### Recover

An operator pauses a deployment to stop offers derived from that deployment, investigates failures, and either resumes, cancels, or creates an explicit rollback deployment. Pins are independent of deployment pause. To stop all new offers of a release, including pin-derived offers, the operator withdraws the release. Neither pause nor withdrawal can recall bytes already downloaded or stop an installation already running.

## 3. Domain and behavior

| Concept | Meaning and invariants |
| --- | --- |
| Customer | An internal organizational boundary for computers and deployment selection. |
| Computer | A Relay-issued identity belonging to one customer, with enabled/revoked state and last-seen information. Hostnames are descriptive, not identity. |
| Application | A stable catalog identity with a name and update policy. |
| Release | An immutable published version of one application, with notes and Windows package variants. SemVer product versions are unique within an application, ignoring build metadata for uniqueness. |
| Package | An immutable object/version reference, byte size, SHA-256 digest, architecture, four-part file version, signature metadata, and declarative installer hints. No arbitrary server-executed commands. |
| Channel | An application-specific distribution track with an optional, explicitly selected current release that supplies the standing update policy. |
| Assignment | One application and channel for a computer, optionally pinned to a published release of that application. |
| Deployment | A release, application/channel, frozen target membership, rollout percentage, and lifecycle state. |
| Installation attempt | An identifiable attempt linking a computer, application, release, and offer to reported progress and outcome. |

A computer may have several applications but only one assignment per application. Releases initially support Windows x64 and ARM64 variants; missing architecture support yields an explicit incompatibility reason. Relay treats package content as opaque; the agent owns installation, local scheduling, privilege handling, signature validation, reboot behavior, and recovery execution.

### Versioning

Each application has one release line. Channels control distribution within that line; there is no separate release-line assignment or migration model.

The product version follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html), including prerelease precedence. Every separately deployable release requires a new product version. Build metadata does not affect precedence, and releases differing only in build metadata are rejected as duplicates within an application, including after withdrawal. A package's four-part file version (for example, `2.4.1.123`) identifies the exact build for diagnostics; it never determines update ordering. Architecture variants belong to the same product release and may have different file versions. Rebuilding a published package requires a new product version, not just a higher file version.

### Release lifecycle

Draft → published → withdrawn. Drafts can change; published content cannot. Publishing requires a completed upload, server-verified size and digest, and required signer metadata. Replacing bytes requires a new release. Withdrawal is irreversible in v1; references and historical outcomes remain available. Artifact signing occurs outside Relay; a checksum alone is not publisher authentication.

### Channel current release

An operator explicitly makes a published release current for an application's channel. This audited, revision-checked action is independent of publishing a release, advancing rollout waves, and completing a deployment; none of those actions changes the current release automatically. A channel can initially have no current release. Selecting a release requires that it belong to the same application and remain published.

The current release is available to all enabled computers assigned to the application/channel, including newly joined computers and computers returning after being offline. A deployment controls temporary, targeted distribution of a candidate; the current release supplies the standing policy outside that candidate's eligible cohort. Making a candidate current intentionally makes it available beyond the deployment snapshot and rollout percentage, so the operator must review that channel-wide effect. Current-release changes use optimistic concurrency and are recorded in audit history.

For example, stable can remain current at `2.5.0` while a deployment offers `2.6.0` to successive waves. New computers and computers outside those waves receive `2.5.0`. Making `2.6.0` current allows them to catch up, even after the deployment is completed. Completing a deployment alone leaves current unchanged. Withdrawal blocks a current release without implicitly selecting its predecessor; an operator must explicitly choose a replacement.

### Deployment lifecycle

Draft → active ↔ paused → completed or cancelled. Completed and cancelled deployments are terminal. Completion is an explicit operator action; it does not assert that offline computers installed successfully. Pause and resume preserve membership and bucketing. Pause blocks new offers and download URL refreshes derived from that deployment; it does not block independently authorized pin-derived or channel-current offers. Cancelling likewise stops deployment-derived offers without removing pins or changing the current release. Completing a deployment stops deployment-derived offers and preserves its history; remaining computers use channel-current policy unless pinned. There is at most one nonterminal activated deployment per application/channel; draft deployments may coexist. Completing or cancelling the old deployment permits a replacement.

Activation snapshots matching customer computers with the appropriate application/channel assignment. Computers enrolled later are outside the deployment snapshot and use channel-current policy unless pinned; they require a subsequent deployment only to join another targeted rollout. Revocation, reassignment, or release withdrawal can remove eligibility after activation; snapshot membership never overrides current authorization.

Assign each target a stable bucket using a versioned hash of deployment ID and computer ID. A percentage admits a corresponding prefix of buckets. Promotion can only increase the percentage; pause handles containment. Promotions retain previously admitted targets. Small fleets may not match percentages exactly: show actual admitted counts before activation and promotion.

### Update decision

For each assigned application, authoritative evaluation checks enabled computer state, assignment, compatibility, publication status, and policy. Select a release in this order:

1. An explicit assignment pin.
2. An active deployment candidate, if the computer remains an eligible snapshot member in the admitted rollout percentage.
3. The assigned channel's current release.

If no source selects a release, return noApplicableRelease with no package offer; this never means uninstall. A selected release must pass publication, compatibility, and version checks. If it is withdrawn or incompatible, return the corresponding reason without silently falling back to another source. Pins are audited privileged actions and shown separately from deployment progress; removing a pin restores deployment/current-release evaluation.

Pause blocks the paused deployment as a source and blocks refresh of its existing offers, but independent pins and current-release policy remain available. Withdrawal overrides every source: a pin, eligible deployment candidate, or current pointer targeting that release produces releaseWithdrawn with no offer or fallback. Withdrawal also blocks download URL refreshes from every source.

Deployments, pins, and current-release policy select exact releases; publishing a higher version never deploys it automatically. Compare the selected product version to the agent-reported installed product version using SemVer precedence: higher is an upgrade, equal is upToDate, and lower is a downgrade. A downgrade requires explicit rollback authorization on the selecting policy (deployment, pin, or current-release change) plus agent-reported downgrade capability; otherwise return incompatible with a rollback-related reason. Returning from a completed, cancelled, or paused rollout to an older current release must not implicitly authorize a downgrade. No selection source bypasses this check.

Agents report installation presence, installed product version, and four-part file version, with a Relay release ID when known. An explicitly absent application is eligible for a fresh installation. An installed application with a missing or invalid product version receives incompatible with an installedVersionUnknown reason and no package offer until its inventory is reconciled. A valid installed product version can be compared even when Relay does not recognize its release ID. A differing file version alone never triggers an update or a same-version replacement.

Distinguish desired release, last offered release, and last reported installed release. An offer is neither proof of download nor installation. Every offer has an ID and policy revision. Refreshing download access re-evaluates current eligibility. Already issued URLs remain usable until expiry.

## 4. API product contract

Both APIs use HTTPS, versioned `/v1` routes, UTC timestamps, opaque IDs, bounded pagination, and structured errors containing code, safe message, and correlation ID. Authentication failures use 401; denied access uses 403; missing resources use 404 without disclosing inaccessible objects; conflicts use 409; throttling uses 429 with retry guidance. Administrative updates use revision-based optimistic concurrency. Retryable creation and publication operations accept idempotency keys bound to actor and request body; mismatched reuse is a conflict.

| API | Representative capabilities |
| --- | --- |
| Admin | `/customers`, `/computers`, `/applications`, application `/releases` and `/channels`, `/deployments`, `/enrollment-tokens`, `/audit-events`. |
| Admin release actions | Create draft; request upload; verify package; publish; withdraw. |
| Admin channel actions | Explicitly make a published release current, with revision checking and optional rollback authorization; inspect current-release history. |
| Admin deployment actions | Preview targets; activate; promote; pause; resume; complete; cancel. |
| Agent | `POST /v1/enroll`, `POST /v1/check-ins`, `POST /v1/update-checks`, `POST /v1/installation-events`, offer download refresh; `POST /v1/installation-attempts`. |

These are capability boundaries, not a final OpenAPI definition. Host-specific HTTP contracts remain in their API projects; business rules belong in Core and persistence/storage implementations in Infrastructure.

Example update check:

```json
{"applications":[{"applicationId":"app_123","installationState":"installed","installedProductVersion":"2.4.0","installedFileVersion":"2.4.0.123","installedReleaseId":"rel_100"}],"platform":"windows","architecture":"x64","capabilities":{"downgrade":false}}
```

Example per-application decision:

```json
{"applicationId":"app_123","decision":"updateAvailable","releaseId":"rel_101","productVersion":"2.4.1","offerId":"offer_456","policyRevision":7,"package":{"fileVersion":"2.4.1.124","sizeBytes":52428800,"sha256":"<hex-digest>","downloadUrl":"<short-lived-url>","expiresAt":"2026-10-27T23:10:00Z","signer":"<trusted-publisher-reference>"},"nextCheckAfterSeconds":900}
```

Other decisions include upToDate, noApplicableRelease, pinned, releaseWithdrawn, and incompatible, with machine-readable reasons. An eligible pin that passes version and rollback checks produces updateAvailable with pin policy provenance when its product version differs from the installed version. Responses never imply that the agent must install immediately.

Enrollment accepts a bootstrap token and a persistent client-generated enrollment request ID. Repeating an identical request must not allocate another computer. A lost credential response requires a bounded recovery handshake tied to the original enrollment secret; after that window, issue a new token and revoke the abandoned identity. Never return credentials based on request ID alone.

### Installation attempts and delayed telemetry

Before beginning a new installation or retry, the agent creates an attempt through `POST /v1/installation-attempts`, supplying its offer ID and a persistent idempotency key. Relay validates current eligibility and binds the attempt to the authenticated computer, application, release, and offer. It atomically allocates a monotonically increasing attempt number per computer/application and returns that number with an opaque attempt ID. Repeating the same request returns the same attempt without incrementing the counter. Polling or renewing a download URL does not create an attempt. An agent must obtain the attempt before starting installation, but can buffer subsequent events while offline.

Installation events contain event ID, attempt ID, application/release/offer IDs, an increasing sequence number within the attempt, observed time, state, and bounded diagnostic code/message. Relay looks up the authoritative attempt number and validates identity and reference bindings rather than trusting a supplied number. States include offered, downloading, installing, succeeded, failed, and rebootRequired. A retry after a terminal failure creates a new attempt; succeeded and failed are terminal outcomes within an attempt.

Order attempt progress by the server-assigned attempt number first and event sequence second, never by arrival time or device timestamps. Preserve valid delayed events in history, including events for attempts that were authorized before a deployment was paused or a release withdrawn. Only events for the latest allocated attempt may advance current attempt status, and only with a higher sequence and a valid state transition. Duplicate event IDs or identical attempt/sequence retries have no additional effect; conflicting reuse is rejected. For example, a delayed failure from attempt 41 cannot overwrite success from attempt 42. Conditional persistence updates must preserve these rules under concurrent ingestion.

Current attempt progress is distinct from reported installed inventory: allocating a retry does not erase the last installed version. Check-in inventory can reconcile actual installation state without rewriting historical attempt outcomes; delayed check-ins must likewise not regress inventory, using an agent-persisted, monotonically increasing inventory revision per computer. A new inventory revision with different content supersedes the previous snapshot; identical retries are harmless and conflicting reuse is rejected. Neither progress nor inventory ordering relies on the device clock.

## 5. Security and artifact delivery

Use Cognito staff access tokens with validated issuer, audience/client binding, expiry, and permitted role claims. Require MFA for privileged staff. Future browser clients use authorization code with PKCE.

Recommend a separate per-computer random opaque bearer credential for unattended agents. Store only its cryptographic hash, map it to one computer/customer, and authenticate it on every Agent request. Use bounded overlap for credential rotation and authoritative enabled-state checks for revocation. Never ship a fleet-wide secret. Bootstrap tokens are random, hashed, customer-bound, single-use, and expire after 24 hours by default; enrollment, token consumption, and identity creation are atomic.

This deliberately narrows Cognito to people in v1. Cognito supports machine-to-machine app clients, but those require client secrets; choosing one client per device introduces provisioning and lifecycle considerations. This is a recommendation, not a Cognito limitation. See [AWS app client settings](https://docs.aws.amazon.com/cognito/latest/developerguide/user-pool-settings-client-apps.html).

Agent-supplied customer/computer IDs never override authenticated identity. Disable authorizer caching that could extend device revocation. Administrative audit records include actor, action, target, timestamp, correlation ID, and safe change summary; credentials and signed URLs never enter logs.

Store binaries in private, versioned S3 storage with immutable published object references and encryption. Upload/download URLs expire after ten minutes by default, with fresh authorization required to renew. Prevent overwrite of published content. Supply digest, size, and signer information; the external agent must enforce a separately established publisher trust policy rather than trust arbitrary signer metadata. URL expiry is a delivery boundary, not immediate revocation of an active download; see [S3 presigned URL behavior](https://docs.aws.amazon.com/AmazonS3/latest/userguide/using-presigned-url.html).

## 6. Persistence blueprint

A single DynamoDB operational table is a good initial fit because access is primarily keyed lookups, bounded collections, and known fleet listings. Do not force binaries or unlimited logs into it. Use on-demand capacity initially, encryption, point-in-time recovery, and least-privilege API roles.

Illustrative keys; finalize physical projections and index names through an implementation design review:

| Entity collection | PK | SK examples |
| --- | --- | --- |
| Application catalog | `APP#id` | `META`, `RELEASE#id`, `CHANNEL#id` |
| Customer | `CUSTOMER#id` | `META` |
| Computer | `COMPUTER#id` | `META`, `ASSIGNMENT#appId` |
| Deployment | `DEPLOYMENT#id` | `META` |
| Target record | `DEPLOYMENT#id#SHARD#n` | `COMPUTER#id` |
| Attempt events | `ATTEMPT#id` | `EVENT#sequence#eventId` |
| Credential/bootstrap lookup | `CREDENTIAL#hash` or `ENROLLMENT#hash` | `META` |
| Audit | `AUDIT#date#shard` | `timestamp#eventId` |

Use sparse, overloaded GSIs for customer computer listings, application deployment listings, computer attempt history, and status views. Attribute namespaces distinguish collection types. Shard large deployment memberships and time-bucket event listings rather than concentrating the fleet under one hot partition key.

| Access pattern | Strategy |
| --- | --- |
| Authenticate/revoke a computer | Credential lookup plus authoritative computer read. |
| Evaluate update | Keyed computer assignment, application/channel policy, deployment membership, and release reads. |
| List customer computers | Customer-index query with pagination. |
| List releases/channels | Application partition queries. |
| List deployment targets/outcomes | Bounded target-shard queries and materialized summaries. |
| Inspect installation history | Computer history index, followed by keyed attempt reads. |
| Enforce unique version or active deployment | Conditional uniqueness/lock records and transactional state changes. |

GSI results may lag; never authorize enrollment, publication, revocation, or update offers from an index listing. DynamoDB GSIs support only eventual consistency; see [AWS GSI documentation](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/GSI.html). Coordinate multi-item policy changes transactionally and evaluate a consistent policy revision so concurrent pause or assignment changes cannot produce mixed-state decisions. No normal API path may require a table scan.

Keep current computer/assignment state and permanent release/deployment history. Retain detailed telemetry for 90 days and audit events for at least one year by default; export audits to durable S3 archives before expiration. DynamoDB TTL is asynchronous and must never enforce credential expiry. High-volume diagnostic logs, analytics, and longer telemetry retention belong in a later S3-based pipeline. Summary counts are eventually consistent projections, rebuilt from authoritative attempt outcomes when needed; they are not authorization inputs.

## 7. Reliability, visibility, and acceptance

Proposed planning envelope: 10,000 computers, up to ten applications each, 15-minute polling with randomized jitter. Treat check-in and fleet update evaluation as batched requests. Validate synchronized reconnect bursts in load tests. Target 99.9% monthly API availability and p95 metadata responses below one second at that envelope, excluding artifact transfer. These are goals, not measured guarantees.

Recommend exponential backoff with jitter, Retry-After support, bounded payloads, and telemetry batch limits. Computers are stale after three missed expected check-ins, with a minimum 45-minute threshold. Offline or unknown status is never success. The service must not instruct an agent to remove a working application because a dependency is unavailable; return a retryable error and preserve last known installed state.

Expose request errors/latency, throttling, enrollment failures, last-seen age, decision reasons, download authorization failures, and deployment attempt outcomes. Show denominator definitions: snapshot targets, currently eligible, offered, succeeded, failed, pending, and stale. Count a computer once per deployment outcome using its latest relevant attempt; retries remain visible in history. Failure and stale categories can overlap and must not be summed as disjoint totals.

Proposed recovery targets: metadata RPO 15 minutes and RTO four hours, subject to restore drills. Restore processes must reconcile S3 artifact references and rotate/revoke suspect credentials; restoring old data must not silently reactivate previously revoked devices.

Acceptance scenarios:

- A new computer belongs to its token's customer, receives only assigned applications, and cannot access another computer's data.
- A 10% rollout produces stable eligibility across polls; promotion to 25% retains every previously eligible target.
- A computer added after activation receives the channel current release, not the deployment candidate; a reassigned computer loses its former deployment eligibility immediately on authoritative evaluation.
- Pausing blocks deployment-derived offers and URL refreshes; a valid independent pin can still authorize its release. Removing that pin while the deployment is paused returns the computer to channel-current evaluation, or noApplicableRelease if current is unset.
- Withdrawing a release blocks all new offers and URL refreshes for that release, including pins; pinned computers receive releaseWithdrawn without fallback. Revoking a computer blocks all its offers and URL refreshes. Historical evidence remains and in-flight work may finish.
- A pin overrides deployment selection and is visibly attributable; removal returns the assignment to normal policy.
- Missing architecture and unsupported downgrade produce explicit reasons, with no unusable package offer.
- SemVer comparisons are numeric (`2.10.0` is newer than `2.9.0`), and `2.5.0-rc.1` to `2.5.0` is an upgrade.
- A downgrade through a deployment, pin, or current-release policy requires explicit rollback authorization and agent capability; an equal product version produces no offer despite a different file version.
- Duplicate product versions, including versions differing only in build metadata, are rejected; replacing published bytes requires a new product version.
- An absent application can receive a fresh installation; an installed application with unknown/invalid product version cannot receive an automatic update. A valid product version remains comparable without a known Relay release ID.
- Duplicate enrollment retries create one identity; duplicate events do not inflate counts; delayed events do not regress current status.
- Attempt 42 succeeds before a delayed failure for attempt 41 arrives: retain both in history and keep attempt 42 as current. Repeating attempt creation returns the same ID/number, and a retry receives a higher number.
- Out-of-order events within an attempt cannot regress progress or replace a terminal outcome; conflicting sequence reuse and cross-device attempt references are rejected. Delayed inventory snapshots cannot overwrite a higher inventory revision.
- Concurrent publication, promotion, and assignment edits return conflicts or commit consistently, never silently overwriting intent.
- An offline computer remains pending/stale even when every responding computer succeeds.
- A failed artifact verification prevents publication, and published object content cannot be replaced.
- A prior release can be explicitly deployed for recovery without rewriting the original release or installation history.
- With stable current at `2.5.0` and a staged deployment of `2.6.0`, admitted targets receive `2.6.0`; other assigned computers, including new ones, receive `2.5.0`.
- Completing that deployment leaves current at `2.5.0` and does not silently downgrade computers already on `2.6.0`. Explicitly making `2.6.0` current allows new and returning computers to catch up without a new deployment.
- Making a release current is a channel-wide policy change independent of deployment completion or percentage. Withdrawing current stops its delivery without automatic fallback.

## 8. Delivery sequence

1. **Trusted foundation:** Cognito staff roles, customer/application catalog, private artifact pipeline, enrollment, revocation, audit records, and CDK deployment.
2. **End-to-end distribution:** assignments, publication, explicit channel-current selection, update decisions, downloads, check-ins, attempts, and retry-safe telemetry. Validate against a contract test client without building the production Windows agent.
3. **Controlled operations:** deployment snapshots, stable rollout buckets, manual promotion/pause, pins, rollback eligibility, fleet views, alerts, load tests, and restore drills.
4. **Future experiences:** administrative web application first; customer access only after defining customer permissions. Automatic health gates, maintenance windows, advanced analytics, and richer targeting require separate product decisions.

Relay is ready for its first production rollout when one published release can move through a small customer cohort to the intended fleet, with every eligibility decision explainable and every reported outcome traceable to a computer, release, and installation attempt.
