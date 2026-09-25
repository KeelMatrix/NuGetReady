# KeelMatrix NuGetReady

`dotnet pack` succeeding does not prove your release is ready. NuGetReady checks the exact artifact set and package archive, then rehearses isolated consumer restore or tool installation from the artifacts you just built.

## Install

```bash
dotnet tool install --global KeelMatrix.NuGetReady
```

## Support and Requirements

NuGetReady runs on Windows, Linux, and macOS. The installed tool targets `net8.0` and requires the .NET 8 runtime.

Library packages under rehearsal may target frameworks other than .NET 8. NuGetReady builds a consumer for every declared library target framework, runs the consumer when that framework is runnable on the host, and treats non-runnable library target frameworks as build-only.

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

The configuration contains no publishing credentials. The check never publishes packages. Consumer builds and tool smoke commands execute package code or build assets with the caller's permissions, so inspect packages and run rehearsals with appropriate permissions.

## Supported Release Workflow Profile

Workflow-policy `pass` is limited to the closed-world 0.1.0 profile described here. It is not a general interpretation of GitHub Actions or shell behavior.

The release identity is one mechanically checked chain:

1. `on.push.tags` contains exactly one literal tag equal to `v` plus the normalized version in the repository-root `nugetready.json`. A config version of `1.2.3` therefore requires the literal tag `v1.2.3`; a wildcard such as `v*.*.*` is not release-identity proof.
2. Each configured primary and optional symbol filename is exactly `<PackageId>.<version>.nupkg` or `<PackageId>.<version>.snupkg`.
3. Archive inspection verifies the packed nuspec ID and version against that same configuration. A nuspec identity failure prevents workflow policy from passing.
4. The producer uploads only those configured paths under the immutable `validated-release-artifacts` identity, the fresh-runner validator checks that artifact set, and the publisher downloads the same identity and pushes only the configured primary package.

The supported workflow has workflow permissions exactly `contents: read` and three unconditional `ubuntu-latest` jobs: a 30-minute artifact producer, a fresh-runner 10-minute validator, and a 10-minute publisher. The producer has no OIDC token or publishing credential. It checks out `${{ github.sha }}` without persisted credentials, sets up .NET from `global.json`, runs the exact restore, format, Release build, Release test, and pack commands, then immediately uploads the configured package paths. `global.json` must contain only SDK version `8.0.425`, `rollForward: latestPatch`, and `allowPrerelease: false`; `NuGet.config` must contain only the canonical NuGet.org v3 source. The workflow directory, lower-case workflow extension, release-control files, and referenced solution/project paths must exist with exact ordinal casing.

The validator depends only on the producer and starts on a fresh runner with an isolated `/tmp/nugetready-packages` package cache. Before repository checkout, it creates an exact runner-controlled `/tmp/nugetready-acquisition/global.json`, sets up SDK `8.0.425`, downloads the immutable artifact to `/tmp/nugetready-artifacts`, and installs NuGetReady 0.1.0 into `/tmp/nugetready-tool` through an exclusive source selection while `/tmp/nugetready-acquisition` is the working directory. It then checks out `${{ github.sha }}` without persisted credentials and runs the installed absolute apphost as its final step. Ordinary repositories use the explicit NuGet.org source:

```powershell
dotnet tool install KeelMatrix.NuGetReady --version 0.1.0 --tool-path /tmp/nugetready-tool --source https://api.nuget.org/v3/index.json --no-cache --verbosity minimal
/tmp/nugetready-tool/nugetready check --config nugetready.json --artifacts /tmp/nugetready-artifacts --format json
```

NuGetReady's own first release instead creates one exact temporary NuGet configuration and installs with `--configfile /tmp/nugetready-tool.config`. That configuration maps only `KeelMatrix.NuGetReady` to the downloaded candidate and maps transitive dependencies to NuGet.org. This self-check form is supported only for the exact `KeelMatrix.NuGetReady.0.1.0.nupkg` candidate. The fixed resolver, SDK setup, and tool acquisition finish before checkout, and no executable step follows the check.

The publish job depends only on that successful validation job, has permissions exactly `contents: read` and `id-token: write`, and contains exactly three steps: download the original immutable `validated-release-artifacts` identity to `artifacts/release`; unconditional `NuGet/login@v1` Trusted Publishing authentication; and one literal `pwsh` `dotnet nuget push` of the configured primary `.nupkg` using only the login step's temporary `NUGET_API_KEY`. Validation never re-uploads or transforms the artifact. Product and .NET CLI telemetry are disabled in all three jobs.

### Publication Reachability Boundary

NuGetReady enters release-policy evaluation for jobs that can publish or influence publication: direct NuGet or GitHub publication operations; jobs with effective OIDC, repository write, unknown permission, or recognized credential capability; publisher dependencies; and jobs that produce artifacts downloaded on that reachable path. A static tag filter alone does not create publication reachability; once a publication path exists, the workflow must still use the exact `v<configured-version>` tag. Recognized credential capability includes a root `secrets` context reference inside a GitHub expression; complete `vars.*`, `inputs.*`, or `env.*` member references to a recognized credential name; and recognized keys in workflow/job/step `env` maps or action/reusable-workflow `with` maps. The bounded names are `NUGET_API_KEY`, `API_KEY`, `ACCESS_TOKEN`, `AUTHORIZATION`, `PASSWORD`, `SECRET`, and `CREDENTIAL`. Every non-alphanumeric character is removed and the remaining token is compared case-insensitively by exact equality, so `NuGetApiKey`, `nuget.api.key`, `NUGET-API-KEY`, and `NUGET_API_KEY` are equivalent while prefixes and suffixes such as `ApiKeyPath` are not. A `vars`, `inputs`, or `env` member selector that is not a complete static member name is unresolvable and enters the capability boundary; unknown behavior there blocks as unproven rather than passing. The bounded expression-evaluated grammar follows GitHub Actions' Context availability table: workflow `run-name`, `concurrency`, and `env` values; `on.workflow_call.inputs.*.default` and `on.workflow_call.outputs.*.value`; job `name`, `concurrency`, `container`, `continue-on-error`, `defaults.run`, `env`, `environment`, `if`, `outputs.*`, `runs-on`, `secrets.*`, `services`, `strategy`, `timeout-minutes`, and reusable-workflow `with.*`; and step `name`, `run`, `if`, `shell`, `working-directory`, `timeout-minutes`, `continue-on-error`, `env`, and action `with` values, including steps in local composite actions. Every scalar leaf within the named mapping and object fields is traversed. In those fields, a `${{` opener that lacks a matching `}}`, has an unterminated quoted expression member, or contains a nested expression opener is unsupported/unproven and blocks as `error` with exit code `2`, regardless of the referenced context or member name. Non-evaluated literal data such as workflow `name`, trigger branch/path/tag filters, workflow-level `defaults`, and unrelated static configuration does not enter publication policy merely because its text resembles an incomplete expression. Reusable-workflow `secrets` bindings are also credential-bearing. For an ordinary step-based job, environment capability is derived separately for every active step after applying workflow, job, and step overrides. Dependency and artifact edges are followed transitively.

Unknown actions, scripts, expressions, or YAML inside that boundary are blocking unsupported/unproven errors. The same unknown action in a separate branch or pull-request workflow with explicit read-only or empty permissions, no recognized credential binding, no publication operation, and no dependency or artifact path to a publisher stays outside release-policy evaluation. This boundary is derived from capability and reachability, not from an allowlist of CI vendors.

Omitted effective permissions remain unsupported because their token scope depends on enterprise, organization, or repository defaults. Explicit empty and read-only permission maps remain distinct from omission. Every root `secrets` context reference inside a GitHub expression, including whole-context transformations such as `toJSON(secrets)`, is credential-bearing. Literal paths or prose that merely contain the word `secrets` are not context references.

### Fail-Closed Result Mapping

- A deterministic violation of a modeled rule is `fail` with exit code `1`.
- Unknown or unsupported semantics inside the publication boundary, and malformed/incomplete expression framing in a modeled expression-evaluated field, are unproven and block as `error` with exit code `2`.
- Warnings are reserved for observations that cannot create false release confidence.
- A workflow with no publication capability, operation, dependency path, artifact path, or release trigger is outside release policy and remains `not-applicable`.

NuGetReady uses YamlDotNet 18.1.0 for its YAML representation model. It does not interpret arbitrary GitHub Actions, reusable workflows, composite actions, shell or MSBuild behavior, dynamic expressions, aliases/merge keys, wrappers, generated scripts, command-resolution changes, extra release jobs, or GitHub Release creation. Those shapes are not necessarily unsafe, but when they occur inside the publication boundary they are unproven. Simplify and isolate the publication path to the documented profile or accept the explicit unsupported result.

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

Workflow-policy details and the capability/reachability boundary are maintained in [Supported Release Workflow Profile](#supported-release-workflow-profile). When a config is below a repository root, NuGetReady looks for `.git` or `.github` above the config so root workflows are not silently skipped.

See [PRIVACY.md](PRIVACY.md) for the local data boundary and [SECURITY.md](SECURITY.md) for the package-execution safety boundary.
