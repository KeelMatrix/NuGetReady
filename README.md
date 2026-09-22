# KeelMatrix NuGetReady

`dotnet pack` succeeding does not prove your release is ready. NuGetReady checks the exact artifact set and package archive, then rehearses isolated consumer restore or tool installation from the artifacts you just built.

## Install

```bash
dotnet tool install --global KeelMatrix.NuGetReady
```

## Quick Start

```bash
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

NuGetReady validates exact expected `.nupkg`/`.snupkg` names, requiring exactly one primary `.nupkg` per package expectation and at most one associated `.snupkg`. Duplicate primary package identities are rejected as an ambiguous artifact set instead of being silently selected. It also checks package identity/version, authors, description, tags, license, README, icon, repository metadata, dependency groups, related-package versions, library/tool layout and command metadata, portable symbols, and sensitive/internal archive entries. It creates a temporary local feed, controlled `NuGet.config`, a separate package cache for each consumer, and bounded consumer processes. Inherited fallback folders and restore-source overrides are disabled; library package caches and installed tool stores are checked for the versioned canonical `.nupkg`, package-specific provenance sidecar, matching identity, and every extracted payload file—including XML documentation—against the supplied artifact before the consumer is built. Tool asset selection is derived from `tools/<tfm>/any` roots; unsupported or empty selections fail as unproven. Package-under-test IDs are source-mapped to the local feed and public dependencies may resolve from the configured public source. Library consumers are restored and built against a public type from every declared `lib/` or `ref/` target framework; runnable target frameworks also execute the generated consumer. Reports are deterministic in text or versioned JSON format.

Supported package kinds are `library`, `multiTargetLibrary`, and `dotnetTool`. Tool expectations may declare a command and safe smoke arguments. Analyzer-only packages are not supported in this version; build assets are consumed as part of the library rehearsal.

The configuration contains no publishing credentials. When a release workflow is present, NuGetReady applies narrow structural checks for version-tag gating, Trusted Publishing/OIDC permissions, long-lived API keys, exact artifact validation, and broad publish wildcards. Deterministic policy errors block the check; broad wildcard findings are warnings. The check never publishes packages. Consumer builds and tool smoke commands execute package code or build assets with the caller's permissions, so inspect packages and run rehearsals with appropriate permissions. Non-runnable library target frameworks are build-only.

For malformed input, an unavailable artifact directory, or restore/source/cache/tooling infrastructure that prevents a trustworthy rehearsal, NuGetReady returns status `error` and exit code `2` with an actionable diagnostic. A reproducibly unready package, including one with a corrupt library asset or unusable build diagnostic, returns status `fail` and exit code `1`; warnings never convert an unknown result into success. A check that was prevented by an earlier blocking finding is reported as `not-run`, while an inapplicable check is reported as `not-applicable` rather than `pass`.

The version-1 report contract uses the check IDs in this order: `artifact-set`, `archive-metadata`, `archive-layout`, `dependency-groups`, `dependency-coherence`, `archive-security`, `archive-parse`, `workflow-policy`, and `consumer-rehearsal`. Each check reports one of `pass`, `warn`, `fail`, `error`, `not-run`, or `not-applicable`; a skipped check never appears as `pass`. Archive-sensitive path semantics are shared by the tool and pack-time/package inspection guards from [`scripts/package-sensitive-paths.json`](scripts/package-sensitive-paths.json). Manifest-defined extended families reject protected names across file and directory path segments. Binary/assembly extensions are exempted only from that sensitive-name heuristic; the release package inspector separately applies its explicit allowed-entry set, so a legitimate assembly name does not authorize an otherwise unexpected archive entry.

## Privacy and telemetry

NuGetReady uses `KeelMatrix.Telemetry` 0.1.0 for shared activation and at-most-weekly heartbeat signals after a trustworthy completed rehearsal. NuGetReady passes no package IDs, dependency names, repository identity, package contents, source paths, workflow content, failure logs, or configuration content to the shared client. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out for the current process; the shared package's other documented opt-out controls are also honored. This repository disables telemetry for development and CI. See [PRIVACY.md](PRIVACY.md) for the product-specific boundary and the shared package's [privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md) for the shared contract.

## Troubleshooting

- Confirm the config path and artifact directory are the intended repository-local paths.
- Declare every expected package and symbol archive explicitly; broad globs are not accepted.
- Rebuild the package when its filename version and nuspec version disagree.
- The rehearsal uses a fresh per-consumer package cache and HTTP cache on every run; inherited fallback folders and restore overrides are ignored, library cache and installed-tool-store `.nupkg`/`.nupkg.sha512` provenance is checked, and extracted payload bytes (including XML documentation) are checked against the supplied artifact. A tool package without a supported `tools/<tfm>/any` layout is reported as unproven rather than passing.
- Each package expectation must list exactly one primary `.nupkg` and may list one associated `.snupkg`; two primary archives with the same package identity/version fail the artifact-set check.
- Treat a package parsing, filesystem, restore, or process-timeout error as an infrastructure/input failure, not as a passing rehearsal.

Workflow policy checks parse the supported job, permission, environment, step, and tag-trigger structure. Comments and step names are not treated as proof of authentication or artifact validation. The default posture is conservative: release vocabulary, release-shaped triggers or inputs, opaque/unresolvable reusable targets, and job structures whose steps or calls cannot be inspected are reported as limited/unproven warnings. A workflow is quiet only when the supported structure proves it is a non-publication path, such as branch-triggered CI with inspectable jobs and locally resolvable reusable workflows containing no publication step. Reusable workflow jobs and composite actions that may publish packages remain outside this narrow analysis and are reported as limited/unproven warnings rather than as proof of publication or proof that no publication exists. These warnings produce report status `warn` and non-blocking exit code `0`; they do not silently become `pass` or turn an unknown result into success. Deterministic policy errors still block the check with exit code `1`. When a config is below a repository root, NuGetReady looks for `.git` or `.github` above the config so root workflows are not silently skipped.

See [PRIVACY.md](PRIVACY.md) for the local data boundary and [SECURITY.md](SECURITY.md) for the package-execution safety boundary.
