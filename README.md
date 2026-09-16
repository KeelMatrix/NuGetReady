# KeelMatrix NuGetReady

`dotnet pack` succeeding does not prove your release is ready. NuGetReady checks the exact artifact set and package archive, then rehearses isolated consumer restore or tool installation from the artifacts you just built.

## Install and first check

```bash
dotnet tool install --global KeelMatrix.NuGetReady
nugetready check --artifacts ./artifacts/packages
```

The default configuration file is `nugetready.json` in the current directory. The default artifact directory is `artifacts/packages`.

```json
{
  "schemaVersion": 1,
  "packages": [
    {
      "id": "Example.Core",
      "kind": "library",
      "version": "1.2.3",
      "artifacts": ["Example.Core.1.2.3.nupkg"]
    },
    {
      "id": "example-tool",
      "kind": "dotnetTool",
      "version": "1.2.3",
      "artifacts": ["example-tool.1.2.3.nupkg", "example-tool.1.2.3.snupkg"],
      "command": "example-tool",
      "smoke": ["--help"]
    }
  ]
}
```

NuGetReady validates exact expected `.nupkg`/`.snupkg` names, duplicate and unintended artifacts, package identity/version, authors, description, tags, license, README, icon, repository metadata, dependency groups, related-package versions, library/tool layout and command metadata, symbols, and sensitive/internal archive entries. It creates a temporary local feed, controlled `NuGet.config`, isolated `NUGET_PACKAGES`, and bounded consumer processes. Package-under-test IDs are source-mapped only to that local feed; declared public dependencies may resolve from the configured public source. Library consumers are restored and built against a public type from every declared `lib/` or `ref/` target framework; runnable target frameworks also execute the generated consumer. Reports are deterministic in text or versioned JSON format.

Supported package kinds are `library`, `multiTargetLibrary`, and `dotnetTool`. Tool expectations may declare a command and safe smoke arguments. Analyzer-only packages are not supported in this version; build assets are consumed as part of the library rehearsal.

The configuration contains no publishing credentials. When a release workflow is present, NuGetReady applies narrow structural checks for version-tag gating, Trusted Publishing/OIDC permissions, long-lived API keys, exact artifact validation, and broad publish wildcards. Deterministic policy errors block the check; broad wildcard findings are warnings. The check never publishes packages. Consumer builds and tool smoke commands execute package code or build assets with the caller's permissions, so inspect packages and run rehearsals with appropriate permissions. Non-runnable library target frameworks are build-only.

For malformed input or an unavailable artifact directory, NuGetReady returns exit code `2`. A completed check with readiness failures returns `1`; warnings never convert an unknown result into success.

## Troubleshooting

- Confirm the config path and artifact directory are the intended repository-local paths.
- Declare every expected package and symbol archive explicitly; broad globs are not accepted.
- Rebuild the package when its filename version and nuspec version disagree.
- The rehearsal uses a fresh package cache and HTTP cache on every run; a cached or public copy cannot satisfy the package-under-test source mapping.
- Treat a package parsing, filesystem, restore, or process-timeout error as an infrastructure/input failure, not as a passing rehearsal.

See [PRIVACY.md](PRIVACY.md) for the local data boundary and [SECURITY.md](SECURITY.md) for the package-execution safety boundary.
