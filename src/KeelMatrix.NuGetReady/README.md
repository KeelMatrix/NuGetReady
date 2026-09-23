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

NuGetReady performs narrow structural workflow checks over supported YAML jobs, steps, permissions, environments, and tag triggers; it does not publish packages, create releases, or prove arbitrary GitHub Actions behavior. Workflow-policy `pass` means every publication-relevant element in the applicable workflow was provably inspected and no publication-policy failure was found. Literal repository-local scripts and composite actions are recursively inspected through shell `-c`/`--command`, PowerShell `-Command`/`-File`, and `cmd /c` forms with an eight-level and 2 MiB indirect-path bound. The supported safe allowlist is `actions/checkout`, `actions/setup-dotnet`, `actions/upload-artifact`, `actions/download-artifact`, and `NuGet/login`; other active remote actions are limited/unproven because their implementations are outside the local boundary. Expression/environment-derived paths, missing or unreadable paths, opaque commands, and paths beyond the bound are also limited/unproven. A warning remains `warn` with non-blocking exit code `0`; it never proves safety or publication and never becomes `pass` from an unknown result. Deterministic policy errors remain blocking, while workflows outside the applicable policy boundary remain `not-applicable`. The consumer rehearsal is not a sandbox: package code and build assets may execute with the caller's permissions. Use trusted package inputs and an appropriate account.

NuGetReady uses `KeelMatrix.Telemetry` 0.1.0 for shared activation and at-most-weekly heartbeat signals after a trustworthy completed rehearsal. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out; this repository disables telemetry for development and CI. See the [privacy policy](https://github.com/KeelMatrix/NuGetReady/blob/main/PRIVACY.md) and the [shared telemetry policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md) for the data and execution boundaries.

See the [repository README](https://github.com/KeelMatrix/NuGetReady/blob/main/README.md) for the configuration schema, troubleshooting, and complete check-family documentation.
