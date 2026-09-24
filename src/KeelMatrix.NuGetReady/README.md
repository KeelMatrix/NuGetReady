# KeelMatrix.NuGetReady

NuGetReady is a .NET tool that checks the exact NuGet artifacts you built and rehearses isolated consumer restore, build, execution, or tool installation before publication.

## Install

```bash
dotnet tool install --global KeelMatrix.NuGetReady
```

## Quick Start

After `dotnet pack`, run:

```bash
nugetready check --artifacts ./artifacts/packages
```

The default configuration is `nugetready.json` in the current directory. It declares each package ID, kind, version, exact primary `.nupkg`, optional `.snupkg`, and tool smoke command. NuGetReady rejects missing, extra, ambiguous, or filename/version-mismatched artifacts, inspects their metadata and layout, and rehearses them through an isolated local feed and fresh package caches. Reports use the stable states `pass`, `warn`, `fail`, `error`, `not-run`, and `not-applicable`; a blocked check never appears as `pass`.

## Supported Workflow Profile

Workflow-policy `pass` applies only to the documented closed-world release profile. The triggering tag must be the exact literal `v` plus the configured version, and the configured artifact filenames, packed nuspec identity, immutable validated artifact, and exact primary package selected for publication must all carry that same identity. The checker evaluates jobs with publication operations or recognized credential capability and every dependency or artifact producer that can influence them. Credential capability includes root `secrets` context references inside GitHub expressions; reusable-workflow `secrets` bindings; complete recognized members of `vars.*`, `inputs.*`, and `env.*`; and recognized keys in workflow/job/step `env` or action/reusable-workflow `with` maps. For the bounded names `NUGET_API_KEY`, `API_KEY`, `ACCESS_TOKEN`, `AUTHORIZATION`, `PASSWORD`, `SECRET`, and `CREDENTIAL`, every non-alphanumeric character is removed and the remaining token is compared case-insensitively by exact equality. Prefixes and suffixes such as `ApiKeyPath` remain outside. A `vars`, `inputs`, or `env` member selector that is not a complete static member name is unresolvable and enters the capability boundary; unknown behavior there blocks as unsupported/unproven. Malformed/incomplete expression framing is checked only in modeled expression-evaluated fields: workflow `run-name`; workflow/job/step `env` values; job `if`, `runs-on`, and `timeout-minutes`; reusable-workflow `with` and `secrets` values; and step `name`, `run`, `if`, `shell`, `working-directory`, `timeout-minutes`, `continue-on-error`, and action `with` values. Incomplete framing in those fields blocks as `error` with exit code `2`; non-evaluated literals such as workflow/job `name`, trigger filters, and unrelated static configuration do not enter policy merely because they contain expression-like text. Ordinary step-based jobs apply workflow, job, and step environment overrides before evaluating each active step. Literal paths or prose containing `secrets` do not imply the context. Unrelated read-only CI without one of those bindings stays outside release policy.

See the canonical [Supported Release Workflow Profile](https://github.com/KeelMatrix/NuGetReady/blob/main/README.md#supported-release-workflow-profile) for the complete specification and fail-closed result mapping.

## Package-Execution Safety

Consumer rehearsal is not a sandbox. Package code, build assets, and tool smoke commands may execute with the caller's permissions. Use trusted package inputs and an appropriate account. NuGetReady itself does not publish packages and its configuration contains no publishing credentials.

## Privacy

NuGetReady uses `KeelMatrix.Telemetry` for shared activation and at-most-weekly heartbeat signals after a trustworthy completed rehearsal. It does not send package IDs, dependency names, repository identity, package contents, source paths, workflow content, failure logs, or configuration content. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out.

For configuration, check contracts, and troubleshooting, see the [repository README](https://github.com/KeelMatrix/NuGetReady/blob/main/README.md). See [PRIVACY.md](https://github.com/KeelMatrix/NuGetReady/blob/main/PRIVACY.md) for the data boundary and [SECURITY.md](https://github.com/KeelMatrix/NuGetReady/blob/main/SECURITY.md) for security reporting and the execution boundary.
