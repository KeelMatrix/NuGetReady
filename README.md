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

The configuration contains no publishing credentials. NuGetReady provides high-confidence release-workflow certification only for the closed-world profile below. Certification requires the active `--config` path to be the exact repository-root `nugetready.json` used by the supported validator; an alternate config can still drive package checks, but it makes workflow certification unsupported/unproven. Any executable workflow with effective OIDC, publication-capable write permission, or a secret-backed credential enters closed-world evaluation even when it contains only an otherwise ordinary command. A deterministic violation inside the supported profile is `fail`/exit code `1`. A publication-relevant YAML, GitHub Actions, expression, shell, action, artifact, or dependency shape outside the model is unsupported/unproven and is a blocking `error`/exit code `2`, never a warning or a clean result. The check never publishes packages. Consumer builds and tool smoke commands execute package code or build assets with the caller's permissions, so inspect packages and run rehearsals with appropriate permissions. Non-runnable library target frameworks are build-only.

The 0.1.0 supported release profile has one `on.push.tags` trigger with the literal `v*.*.*` pattern, workflow permissions exactly `contents: read`, and three unconditional `ubuntu-latest` jobs: a 30-minute artifact producer, a fresh-runner 10-minute validator, and a 10-minute publisher. The producer has no OIDC or publishing credential and contains exactly an `actions/checkout@v6` checkout of `${{ github.sha }}` without persisted credentials, SDK setup from `global.json`, direct restore/format/Release build/Release test/pack commands, and an immediate `actions/upload-artifact@v4` upload of the exact configured package paths as `validated-release-artifacts`. `global.json` must contain exactly SDK version `8.0.425`, `rollForward: latestPatch`, and `allowPrerelease: false`; additional resolver properties such as `sdk.paths` are unsupported/unproven. `NuGet.config` must contain only the canonical NuGet.org v3 source. The `.github/workflows` directory, lower-case workflow extension, `global.json`, `NuGet.config`, `nugetready.json`, and every referenced solution/project path must exist with exact ordinal casing; host-specific case-insensitive lookup is not accepted as proof for the Ubuntu release path. The solution and project operands may vary only as literal repository-relative `.sln`/`.slnx` and `.csproj` paths; other arguments or producer steps are unsupported/unproven.

The validator depends only on the producer and starts on a fresh runner with an isolated `/tmp/nugetready-packages` package cache. Before repository checkout, it creates an exact runner-controlled `/tmp/nugetready-acquisition/global.json` for SDK `8.0.425` with `rollForward: latestPatch` and `allowPrerelease: false`, sets up that literal SDK, downloads the immutable producer artifact to `/tmp/nugetready-artifacts`, and installs NuGetReady into `/tmp/nugetready-tool` through an exclusive source selection while `/tmp/nugetready-acquisition` is the working directory. It then checks out the exact triggering commit `${{ github.sha }}` without persisted credentials and runs the already-installed absolute apphost as its final step. Ordinary repositories use the explicit NuGet.org source:

```powershell
dotnet tool install KeelMatrix.NuGetReady --version 0.1.0 --tool-path /tmp/nugetready-tool --source https://api.nuget.org/v3/index.json --no-cache --verbosity minimal
/tmp/nugetready-tool/nugetready check --config nugetready.json --artifacts /tmp/nugetready-artifacts --format json
```

NuGetReady's own first release instead creates one exact temporary NuGet configuration and installs with `--configfile /tmp/nugetready-tool.config`. That configuration clears inherited sources, maps the exact `KeelMatrix.NuGetReady` ID only to the downloaded immutable artifact directory, and maps all transitive dependencies to NuGet.org. NuGet's exact-ID mapping precedence prevents another feed from racing the candidate while still resolving its public dependencies. This self-check form is supported only when the artifact set contains the exact `KeelMatrix.NuGetReady.0.1.0.nupkg` candidate; changing the resolver/configuration-generation steps or acquisition working directory is unsupported/unproven. The fixed runner-controlled resolver, SDK setup, and tool acquisition all finish before checkout, and the subsequent check uses the installed absolute apphost. Together with the isolated package cache, this prevents repository `global.json`, aliases, global tools, prior build steps, `GITHUB_PATH`, or `GITHUB_ENV` writes from selecting the validator. No executable step follows the check.

The publish job depends only on that successful validation job, has permissions exactly `contents: read` and `id-token: write`, and contains exactly three steps: download the original immutable `validated-release-artifacts` identity to `artifacts/release`; unconditional `NuGet/login@v1` Trusted Publishing authentication; and one literal `pwsh` `dotnet nuget push` of the configured primary `.nupkg` using only the login step's temporary `NUGET_API_KEY`. Validation never re-uploads or transforms the artifact. Product and .NET CLI telemetry are disabled in all three jobs.

NuGetReady uses YamlDotNet 18.1.0 for its YAML representation model. It does not interpret arbitrary GitHub Actions, reusable workflows, composite actions, arbitrary shell or MSBuild behavior, dynamic expressions, aliases/merge keys, wrappers, generated scripts, command-resolution changes, additional release jobs, or GitHub Release creation. Repository-controlled MSBuild executes only in the unprivileged producer; the supported model understands its publication-relevant result solely as the exact immutable artifact handed to the fresh validator. Those other shapes are not necessarily unsafe, but they are unproven by this version. Simplify and isolate the NuGet publication path to the documented profile or accept the explicit unsupported result.

For malformed input, an unavailable artifact directory, or restore/source/cache/tooling infrastructure that prevents a trustworthy rehearsal, NuGetReady returns status `error` and exit code `2` with an actionable diagnostic. A reproducibly unready package, including one with a corrupt library asset or unusable build diagnostic, returns status `fail` and exit code `1`; warnings never convert an unknown result into success. A check that was prevented by an earlier blocking finding is reported as `not-run`, while an inapplicable check is reported as `not-applicable` rather than `pass`.

The version-1 report contract uses the check IDs in this order: `artifact-set`, `archive-metadata`, `archive-layout`, `dependency-groups`, `dependency-coherence`, `archive-security`, `archive-parse`, `workflow-policy`, and `consumer-rehearsal`. Each check reports one of `pass`, `warn`, `fail`, `error`, `not-run`, or `not-applicable`; a skipped check never appears as `pass`. Archive-sensitive path semantics are shared by the tool and pack-time/package inspection guards from [`scripts/package-sensitive-paths.json`](scripts/package-sensitive-paths.json). Manifest-defined extended families reject protected names across file and directory path segments. Binary/assembly extensions are exempted only from that sensitive-name heuristic; the release package inspector separately applies its explicit allowed-entry set, so a legitimate assembly name does not authorize an otherwise unexpected archive entry.

## Privacy and telemetry

NuGetReady uses `KeelMatrix.Telemetry` 0.1.1 for shared activation and at-most-weekly heartbeat signals after a trustworthy completed rehearsal. NuGetReady passes no package IDs, dependency names, repository identity, package contents, source paths, workflow content, failure logs, or configuration content to the shared client. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out for the current process; the shared package's other documented opt-out controls are also honored. This repository disables telemetry for development and CI. See [PRIVACY.md](PRIVACY.md) for the product-specific boundary and the shared package's [privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md) for the shared contract.

## Troubleshooting

- Confirm the config path and artifact directory are the intended repository-local paths.
- Declare every expected package and symbol archive explicitly; broad globs are not accepted.
- Rebuild the package when its filename version and nuspec version disagree.
- The rehearsal uses a fresh per-consumer package cache and HTTP cache on every run; inherited fallback folders and restore overrides are ignored, library cache and installed-tool-store `.nupkg`/`.nupkg.sha512` provenance is checked, and extracted payload bytes (including XML documentation) are checked against the supplied artifact. A tool package without a supported `tools/<tfm>/any` layout is reported as unproven rather than passing.
- Each package expectation must list exactly one primary `.nupkg` and may list one associated `.snupkg`; two primary archives with the same package identity/version fail the artifact-set check.
- Treat a package parsing, filesystem, restore, or process-timeout error as an infrastructure/input failure, not as a passing rehearsal.

Workflow-policy `pass` certifies only the documented closed-world profile; it is not a general interpretation of GitHub Actions or shell semantics. Unknown or unclassified publication-relevant nodes satisfy the implementation's fail-closed meta-invariant by producing a blocking unsupported/unproven `error`. Warnings are reserved for observations that cannot create false release confidence. Workflows with no applicable publication path remain `not-applicable`. When a config is below a repository root, NuGetReady looks for `.git` or `.github` above the config so root workflows are not silently skipped.

See [PRIVACY.md](PRIVACY.md) for the local data boundary and [SECURITY.md](SECURITY.md) for the package-execution safety boundary.
