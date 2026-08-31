# Build and release

## Build the NuGet MCP package

Create the primary package and its platform-specific dependencies:

```bash
dotnet pack src/ComfyUi.Mcp/ComfyUi.Mcp.csproj \
  --configuration Release \
  --output artifacts/packages
```

Test the installed package through `dnx`:

```bash
bash scripts/smoke-test-package.sh artifacts/packages
```

The smoke test initializes the packaged MCP server and calls `workflow_list` to
verify that bundled workflows and configuration files resolve correctly.

## Version alignment

A release tag must match all three values:

- `<Version>` in `src/ComfyUi.Mcp/ComfyUi.Mcp.csproj`
- Top-level `version` in `.mcp/server.json`
- `packages[0].version` in `.mcp/server.json`

The Registry name in `.mcp/server.json` and the hidden `mcp-name` comment in
the packaged README must also match exactly, including letter case.

## Trusted publishing

The release workflow uses GitHub OIDC rather than a long-lived NuGet API key.
Configure:

- A NuGet trusted-publishing policy for `release.yml`
- GitHub Actions environment `production`
- Repository variable `NUGET_USER` containing the NuGet.org username

The NuGet policy's workflow filename and environment must be `release.yml` and
`production` exactly.

## Trigger a release

After committing and pushing the aligned version:

```bash
git tag v0.0.1-preview5
git push origin v0.0.1-preview5
```

Tags matching `v*` trigger `.github/workflows/release.yml`. The workflow:

1. Runs tests and validates the installed NuGet package.
2. Builds self-contained Windows, Linux, and macOS archives.
3. Exchanges GitHub OIDC identity for a temporary NuGet credential.
4. Publishes all `.nupkg` files to NuGet.org.
5. Publishes `.mcp/server.json` to the official MCP Registry.
6. Creates a GitHub release containing the packages and executable archives.

NuGet package versions are immutable. If publication fails after the primary
package is accepted, fix the issue and publish a new prerelease version rather
than reusing the existing version.
