# Storage and staff identity foundation

Issue [#6](https://github.com/jtron5050/relay/issues/6) provisions only the data and
staff identity foundation. It does not deploy the APIs, IAM workload roles,
archive workers, artifact publication, or JWT authorization. The API hosts still
expose their existing public health/conventions surface.

## Synthesis and tests

Install .NET 10, a supported Node.js LTS runtime (22 or 24), and the AWS CDK v2 CLI
compatible with Amazon.CDK.Lib 2.274.0 (validated with CLI 2.1147.0). Node is also required by the .NET CDK
assertion tests through jsii. Run from the repository root:

```sh
dotnet build Relay.slnx
dotnet test Relay.slnx
cdk synth --no-lookups
```

Synthesis and CDK tests need no AWS credentials or deployed resources. The full
persistence suite additionally needs DynamoDB Local; see [persistence.md](persistence.md).
Cloud assembly output is ignored under `cdk.out/`. No AWS lookups, context account
IDs, explicit globally unique bucket names, credentials, users or passwords are
embedded. For a later authorized deployment, select an AWS account/region and
profile, bootstrap its CDK environment, review the synthesized change set, and
provide deployment permissions. No deployment is part of this change.

## Resources and retention

The operational table has string `PK`/`SK`, on-demand billing, AWS-managed KMS
encryption, 35-day PITR, deletion protection, old/new-image Streams, and sparse
`GSI1`/`GSI2` string key pairs. Both indexes use `KEYS_ONLY`, a deliberately narrower
projection than the ADR permits: the existing adapter rehydrates base records and
no payload, credential hashes, diagnostic data or signed URLs enter the indexes.
Streams have AWS's 24-hour retention; downstream consumers and recovery belong to
later slices.

DynamoDB supports one TTL attribute per table: numeric `expiresAt`, Unix seconds,
matching the existing receipt serializer. Feature writers must apply these rules:

| Data | TTL rule |
| --- | --- |
| Ordinary idempotency receipts | Logical expiry at 24 hours; existing adapter writes `expiresAt`. |
| Detailed telemetry and event deduplication | Receipt time +90 days; later feature adapters must write the top-level numeric attribute. |
| Audit events | Omit TTL initially. Only after a verified durable archive receipt exists, set `expiresAt` no earlier than event time +365 days and archive confirmation time. Archive failure leaves the event online. |
| Compact attempts/outcomes, attempt bindings, release/deployment history and uniqueness reservations | No TTL; retain permanently. |
| Credentials, leases and enrollment | Always enforce validity authoritatively in code; physical TTL deletion never authorizes access or releases a lock. |

Enabling TTL does not implement telemetry writers or an archive pipeline. The
current adapter only serializes receipt TTL; the future audit/archive worker must
confirm the exact archive object version, digest and durable receipt before
scheduling audit expiration. Do not add TTL to audit records at creation.

Two S3 buckets hold artifacts and audit archives separately. Both use SSE-S3,
versioning, all public-access blocks, bucket-owner-enforced ownership (ACLs off),
and bucket policies denying non-TLS access to both bucket and object operations.
The only lifecycle action aborts incomplete multipart uploads after seven days;
no current/noncurrent versions expire. New object versions do not rewrite the
old version's bytes. Later publication must persist exact version IDs and use
least-privilege staging/published-prefix permissions to prevent deletion or
replacement of published versions. This slice grants no upload or delete roles;
versioning alone is not an object-lock or publication authorization guarantee.

The table, buckets and user pool use `Retain` for deletion and replacement in all
environments, including production. The table and pool also enable service-level
deletion protection. A later stack removal therefore leaves data and identity
resources, with ongoing costs. Operators must plan explicit recovery/import and
separately authorize any eventual destruction. Retention does not prevent a
privileged principal from deleting individual records/versions; workload IAM is
part of subsequent API/worker provisioning. No automatic cleanup or archive
expiration is configured.

## Staff access administration

The Cognito pool disables self-signup and self-service account recovery. Usernames
are case-insensitive. All staff, including viewers, must enroll software TOTP MFA;
this enforces privileged MFA without relying on a group-conditional policy.
Passwords require 14 characters and all four character classes; temporary
passwords last one day. No SMS delivery configuration or SES setup is required.

An authorized identity administrator uses the Cognito console or AWS CLI/SDK to:

1. Verify the person's identity and create a named staff user with
   `AdminCreateUser` (use a secure temporary-password delivery process; use
   `MessageAction=SUPPRESS` when delivering out of band).
2. Add the user to exactly one of `Administrator`, `ReleaseOperator`, or
   `SupportViewer` with `AdminAddUserToGroup`, following the
   [HTTP permission matrix](api-v1.md#permission-matrix). These are Relay claims,
   not IAM roles; groups grant no direct AWS access.
3. Complete first-login password change and TOTP setup with controlled Cognito
   SRP-capable tooling, handling `NEW_PASSWORD_REQUIRED`, `MFA_SETUP` and
   `SOFTWARE_TOKEN_MFA` challenges. Do not issue staff credentials through Relay.
4. For a role change, remove obsolete group membership before adding the new
   group. For departure, disable the user (`AdminDisableUser`) and globally sign
   out (`AdminUserGlobalSignOut`). Recover lost MFA only after identity verification
   through the identity administrator's documented recovery process; no public
   recovery endpoint is enabled.

Grant these Cognito administrative actions only to authorized identity operators,
scoped to the pool ARN; normal staff groups do not imply identity administration.
This stack creates no initial administrator or administrative IAM principal.
Cognito group names cannot contain spaces: `ReleaseOperator` maps to the product
role “Release operator” and `SupportViewer` to “Support viewer”. Future API role
mapping must use these exact claim values.

Changes to staff access should be monitored through the organization's CloudTrail
and identity operations process; this slice does not provision that pipeline.

The public app client has no secret, permits only SRP and refresh-token auth,
hides user-existence errors and enables token revocation. Access/ID tokens last
15 minutes; refresh tokens last eight hours; authentication challenges last three
minutes. Tooling should retain tokens only in protected storage and clear them
on sign-out. Offline JWT validation cannot detect revocation or group changes
until token expiry without an additional authoritative check. Future Admin API
authorization must validate signature/JWKS, issuer, `token_use=access`, expiry,
allowed `client_id`, required scopes and exact Relay groups/permissions. An ID
token is never an API credential. Authentication/authorization remains a separate
backlog slice; no protected routes are added here.

## Future browser client / PKCE

No UI, Cognito domain, callback URLs, implicit grant or client-credentials grant
is configured. When the real staff UI origin and sign-out URI are known, add a
separate public client (no secret), a Cognito domain, exact HTTPS redirect/logout
allowlists and authorization-code flow with PKCE S256. Require `state` and OIDC
`nonce`, request minimal scopes, define the Admin API resource-server scopes,
and register that client ID with the API authorizer. SRP access tokens do not
carry custom OAuth resource-server scopes; do not weaken scope checks to reuse
them for those future endpoints. Do not enable implicit/password grants or use
wildcard production callbacks. Review the staff token/session policy at that time.

## Nonsecret outputs

`OperationalTableName`, `OperationalTableStreamArn`, `ArtifactBucketName`,
`AuditArchiveBucketName`, `StaffUserPoolId`, `StaffIssuer`, and `StaffClientId`
are configuration identifiers. Use the table name for `DynamoOptions.TableName`;
future API provisioning consumes the pool issuer and allowed client ID.
No passwords, client secrets, credentials, signed URLs or user data are output.
