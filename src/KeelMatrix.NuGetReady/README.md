# KeelMatrix.NuGetReady

NuGetReady is a .NET tool that rehearses a NuGet release from the exact package artifacts you built before you publish them.

## Install

```bash
dotnet tool install --global KeelMatrix.NuGetReady
```

## Quick Start

After `dotnet pack`, run:

```bash
nugetready check --artifacts ./artifacts/packages
```

The check requires exactly one primary `.nupkg` for each package expectation and permits at most one associated `.snupkg`; duplicate primary package identities are rejected rather than selected by order. It inspects archive metadata and layout, and uses an isolated local feed and per-consumer package cache. Fallback package folders and inherited restore-source overrides are disabled. For libraries, the restored package-version cache's canonical `.nupkg` and package-specific `.nupkg.sha512` metadata are checked against the supplied artifact, then the expanded payload—including XML documentation—is compared before use. For tools, the installed store's versioned canonical archive, provenance sidecar, package identity, and full expanded payload are checked against the supplied artifact; supported `tools/<tfm>/any` asset roots are derived from the package, and missing or unsupported layouts fail as unproven rather than passing. Library consumers are restored and built against a public type from every declared `lib/` or `ref/` target framework; runnable target frameworks also execute the generated consumer. .NET tools are installed and run with their configured smoke arguments. Archive-sensitive names are rejected as exact names and as their manifest-defined extended families across file and directory path segments. Binary/assembly extensions are exemptions only from the sensitive-name heuristic; the explicit package-content allowlist still rejects undeclared binary or XML payloads.

Reports use the stable check states `pass`, `warn`, `fail`, `error`, `not-run`, and `not-applicable`. Missing, ambiguous, malformed, or blocked inputs leave dependent checks explicitly skipped; they do not produce a false `pass`. The complete check ID and report contract is documented in the [repository README](https://github.com/KeelMatrix/NuGetReady/blob/main/README.md).

On Unix, bounded child work starts in a dedicated process group before the target program runs. Successful completion, timeout, and cancellation terminate or verify that group within a bounded period; Windows uses the existing job-object cleanup path.

## Limitations and safety

NuGetReady provides high-confidence workflow certification only for a closed-world release profile: one literal `v*.*.*` tag trigger; workflow `contents: read`; and separate unconditional `ubuntu-latest` producer, fresh-runner validator, and publisher jobs. The producer checks out the exact triggering commit `${{ github.sha }}`, uses the exact SDK/restore/format/Release build/Release test/pack language, and immediately uploads the exact package set as the immutable `validated-release-artifacts` identity. Its `global.json` must contain only SDK version `8.0.425`, `rollForward: latestPatch`, and `allowPrerelease: false`. Before checkout, the validator creates the exact runner-controlled `/tmp/nugetready-acquisition/global.json` with the same closed SDK resolver, sets up literal SDK `8.0.425`, uses an isolated package cache, downloads the artifact identity to `/tmp/nugetready-artifacts`, and acquires NuGetReady 0.1.0 through an exclusive source selection into `/tmp/nugetready-tool` from that resolver directory. It then checks out `${{ github.sha }}` and runs `/tmp/nugetready-tool/nugetready check --config nugetready.json --artifacts /tmp/nugetready-artifacts --format json` as its final step. Normal repositories use only the explicit NuGet.org source. This repository's self-check creates an exact temporary NuGet configuration that maps only the tool ID to the immutable candidate artifact and maps transitive dependencies to NuGet.org; any resolver, acquisition-directory, or source variation is unsupported/unproven. The `.github/workflows` directory, lower-case workflow extension, `global.json`, `NuGet.config`, `nugetready.json`, and referenced solution/project paths must exist with exact ordinal casing. The publisher depends on validation, downloads the original producer artifact without transformation, and contains only that download, unconditional `NuGet/login@v1`, and one literal direct push using the temporary login output. All three jobs disable product and .NET CLI telemetry.

NuGetReady uses YamlDotNet 18.1.0 for its YAML representation model and validates both the repository and runner-controlled `global.json` shapes exactly. It does not claim to interpret arbitrary GitHub Actions, shell, composite/reusable workflow, dynamic expression, alias/merge-key, arbitrary MSBuild, wrapper, generated-script, command-resolution, additional release-job, or GitHub Release semantics. Repository-controlled build behavior is confined to the unprivileged producer; validator SDK setup and tool acquisition occur before the exact-commit checkout under the fixed runner-controlled resolver, and only the immutable artifact output crosses from the producer. A deterministic violation of the supported profile is `fail`/exit code `1`. An unknown or unsupported publication-relevant node is unproven and blocks with `error`/exit code `2`; it is never treated as safe or downgraded to a warning. Unconventional workflows must simplify and isolate their publication path to this profile or accept the explicit unsupported result. The consumer rehearsal is not a sandbox: package code and build assets may execute with the caller's permissions. Use trusted package inputs and an appropriate account.

NuGetReady uses `KeelMatrix.Telemetry` 0.1.1 for shared activation and at-most-weekly heartbeat signals after a trustworthy completed rehearsal. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out; this repository disables telemetry for development and CI. See the [privacy policy](https://github.com/KeelMatrix/NuGetReady/blob/main/PRIVACY.md) and the [shared telemetry policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md) for the data and execution boundaries.

See the [repository README](https://github.com/KeelMatrix/NuGetReady/blob/main/README.md) for the configuration schema, troubleshooting, and complete check-family documentation.
