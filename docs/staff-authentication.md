# Staff authentication

The Admin API requires these nonsecret settings in every environment, including
Development. Missing or invalid settings fail startup; discovery is fetched only
when validating tokens, so startup and public health need no Cognito connection.

```sh
export StaffAuthentication__Issuer=https://cognito-idp.REGION.amazonaws.com/POOL_ID
export StaffAuthentication__ClientId=CLIENT_ID
dotnet run --project src/Relay.AdminApi
```

Use CDK outputs `StaffIssuer` and `StaffClientId`. Bearer tokens must be signed
RS256 access tokens from that exact issuer with matching `client_id`, nonempty
`sub`, and valid expiry/lifetime (30 seconds clock skew). ID tokens are rejected.
The handler caches discovery/JWKS and refreshes signing keys automatically. It
never accepts signing keys or discovery addresses supplied by callers.

Only `/health` and `/openapi/v1.json` are public. Default and fallback policies
deny all access: missing/invalid credentials yield 401; valid tokens without
permission yield 403. This includes unmatched routes (rather than anonymous 404).
Errors retain the shared safe JSON envelope, correlation ID and no-store headers;
401 includes a Bearer challenge without validation details.

Future handlers must use `.RequireAuthorization("fleet.read")` or another exact
permission from [the matrix](api-v1.md#permission-matrix). Bare
`RequireAuthorization()` and unannotated endpoints deny even Administrators.
Do not add anonymous feature routes. Named policies authorize exact, case-sensitive
`cognito:groups` values `Administrator`, `ReleaseOperator`, and `SupportViewer`.
Groups are additive; unknown groups and caller-supplied permission claims grant
nothing. Use `sub` as the stable actor identifier. Resource-specific authorization
(including operation status and additional rollback permission), revisions,
idempotency and audit belong to their future feature handlers.

The existing SRP client does not issue custom Relay OAuth scopes. Its Cognito
self-service scope is not a Relay permission and never grants Administrator
access. Custom scopes and browser OAuth/PKCE remain future work.

Follow [staff provisioning](infrastructure.md#staff-access-administration) to
create users and assign groups. The existing pool requires TOTP MFA for all staff;
the API does not infer MFA from invented access-token claims. Offline JWT
validation cannot immediately observe role changes, user disablement or token
revocation: already issued tokens may remain usable until their 15-minute expiry
plus clock skew. No online revocation lookup is implemented.

Authentication integration tests use local RSA-signed JWTs through the production
bearer handler, with test-only discovery/signing configuration and routed probes.
They need no AWS account, credentials or deployed pool. Full solution checks also
require Node.js for CDK and DynamoDB Local as described in persistence.md.
