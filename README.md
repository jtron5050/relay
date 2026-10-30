# Relay

ASP.NET Core APIs for administrators and computer updater agents, with AWS infrastructure defined in CDK.

## Projects

- `Relay.AdminApi`: administrator HTTP API; feature folders for computers, releases, and deployments.
- `Relay.AgentApi`: updater agent HTTP API; feature folders for registration, check-in, and update checks.
- `Relay.Core`: shared business models and rules. No API or infrastructure dependencies.
- `Relay.Infrastructure`: persistence and storage implementations; references Core.
- `Relay.Cdk`: AWS infrastructure. The stack is currently empty.

Both API hosts reference Core and Infrastructure. Keep HTTP contracts and authentication in their respective hosts. Add shared implementations when actual features need them; the library folders are intentionally empty.

## Development

```sh
dotnet build Relay.slnx
dotnet run --project src/Relay.AdminApi
dotnet run --project src/Relay.AgentApi
```

The launch profiles use `http://localhost:5100` (admin) and `http://localhost:5200` (agent). Each exposes `GET /health`.

Both APIs use `Amazon.Lambda.AspNetCoreServer.Hosting` with API Gateway HTTP API events and can run locally with Kestrel. Publish each API separately for deployment to its own Lambda. CDK packaging, Lambda resources, gateways, IAM roles, and authentication configuration remain to be implemented. Only the public health endpoint is currently mapped; configure authentication and authorization before adding protected operations.

## Testing and CI

Install the .NET 10 SDK and allow NuGet access for the initial restore. `global.json`
selects a stable .NET 10 SDK (10.0.100 or a later .NET 10 feature band).
From the repository root, run the same checks as CI:

```sh
dotnet restore Relay.slnx
dotnet build Relay.slnx --configuration Release --no-restore
dotnet test Relay.slnx --configuration Release --no-build --no-restore
```

`tests/Relay.AdminApi.Tests` and `tests/Relay.AgentApi.Tests` use xUnit and
ASP.NET Core's `WebApplicationFactory` to start each real host in an in-memory
test server. Health smoke tests cover Development and Production, checking that
an unauthenticated `GET /health` returns HTTP 200, JSON content, and
`{"status":"healthy"}`. The clients use an HTTPS base address and disable redirects
so a redirect cannot hide a route failure. No running APIs, trusted development
certificate, AWS credentials, Docker, or deployed resources are required.

To check the local Kestrel endpoints separately, start each API with the development
commands above, then run:

```sh
curl --fail-with-body http://localhost:5100/health
curl --fail-with-body http://localhost:5200/health
```

The [CI workflow](.github/workflows/ci.yml) runs on pushes, pull requests, and manual
dispatch. It installs .NET 10, restores and builds the entire solution, and runs
both test projects; any failed step fails the job. The workflow requires no
repository secrets and has read-only repository access. Add feature-specific test
projects under `tests/` to `Relay.slnx` as business behavior is implemented.
