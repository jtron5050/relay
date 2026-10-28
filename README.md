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

Add test projects under `tests/` as business behavior is implemented.
