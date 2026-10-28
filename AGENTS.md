# Repository Guidelines

## Project Structure & Module Organization

Relay is a .NET 10 service for distributing application updates to customer computers. `Relay.slnx` contains five projects under `src/`:

- `Relay.AdminApi`: administrator HTTP endpoints and authentication.
- `Relay.AgentApi`: updater-agent HTTP endpoints and authentication.
- `Relay.Core`: shared business models and rules; keep it independent of API and infrastructure projects.
- `Relay.Infrastructure`: persistence and storage implementations; references Core.
- `Relay.Cdk`: AWS CDK infrastructure; the stack is currently empty.

Organize API features into folders such as computers, releases, and deployments. Keep HTTP contracts in the respective API host. Read `docs/product-spec.md` for intended behavior, distinguishing its proposed design from implemented capabilities. No frontend or asset pipeline exists yet.

## Build, Test, and Development Commands

Use the .NET 10 SDK from the repository root:

- `dotnet restore Relay.slnx`: restore NuGet dependencies.
- `dotnet build Relay.slnx`: build all projects.
- `dotnet run --project src/Relay.AdminApi`: run the admin API at `http://localhost:5100`.
- `dotnet run --project src/Relay.AgentApi`: run the agent API at `http://localhost:5200`.

Both APIs expose `GET /health`. The `cdk.json` app path currently references a nonexistent project; correct it before using CDK CLI commands.

## Coding Style & Naming Conventions

Follow existing C# style: four-space indentation, file-scoped namespaces, PascalCase types and public members, and camelCase locals and parameters. Match namespaces to project and folder names. Preserve nullable reference types and implicit usings. Project XML uses two-space indentation. No repository-specific formatter or linter is configured; keep formatting consistent with adjacent code.

## Testing Guidelines

No test projects, test framework, or coverage threshold are configured. Add tests under `tests/` as behavior is implemented, using names such as `Relay.Core.Tests` and `ReleaseTests.cs`, and include projects in `Relay.slnx`. Run `dotnet test Relay.slnx` once tests exist. For API changes, verify affected endpoints locally and document results.

## Commit & Pull Request Guidelines

History currently uses short, plain summaries such as `create product spec`; no formal commit convention is established. Write concise, imperative summaries. Keep pull requests focused, describe behavior changes, link relevant issues, and record build/test results. Update documentation when contracts or configuration change. Configure authentication and authorization before adding protected operations; keep credentials out of source control.
