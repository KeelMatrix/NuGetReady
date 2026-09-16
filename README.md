# KeelMatrix NuGetReady

`dotnet pack` succeeding does not prove your release is ready. NuGetReady checks the exact artifact set and package archive before publishing.

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

NuGetReady validates exact expected `.nupkg`/`.snupkg` names, duplicate and unintended artifacts, package identity/version, authors, description, tags, license, README, icon, repository metadata, dependency groups, library/tool layout and command metadata, symbols, and sensitive/internal archive entries. Reports are deterministic in text or versioned JSON format.

The configuration contains no publishing credentials. Publishing, clean consumer restore/install, package execution, release-workflow checks, and telemetry are outside this milestone. Package code and build assets are executable content; inspect packages and run future consumer rehearsals with the permissions appropriate to the package.

For malformed input or an unavailable artifact directory, NuGetReady returns exit code `2`. A completed check with readiness failures returns `1`; warnings never convert an unknown result into success.

## Troubleshooting

- Confirm the config path and artifact directory are the intended repository-local paths.
- Declare every expected package and symbol archive explicitly; broad globs are not accepted.
- Rebuild the package when its filename version and nuspec version disagree.
- Treat a package parsing or filesystem error as an infrastructure/input failure, not as a passing rehearsal.

See [KeelMatrix.NuGetReady.md](KeelMatrix.NuGetReady.md) for the product contract and [PRIVACY.md](PRIVACY.md) for the local data boundary.
