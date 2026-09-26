# KeelMatrix.NuGetReady

NuGetReady is a .NET tool that checks the exact NuGet artifacts you built and rehearses isolated consumer restore, build, execution, or tool installation before publication.

## Install

```bash
dotnet tool install --global KeelMatrix.NuGetReady
```

## Support and Requirements

NuGetReady runs on Windows, Linux, and macOS. The installed tool targets `net8.0` and requires the .NET 8 runtime. Consumer rehearsal also requires a compatible .NET SDK for restore, build, and tool installation; unavailable SDKs, runtimes, target packs, or workloads are `error`/exit code `2`.

The supported matrix is explicit: `net5.0` and later unqualified modern .NET targets run on the host; Windows-specific modern targets run only on Windows; `.NET Standard` targets are build-only; and .NET Framework targets such as `net48` and `net481` are build-only on Windows. Other or platform-incompatible targets are unsupported infrastructure (`error`/exit code `2`). Build-only validation proves compilation and package consumption, not execution.

## Quick Start

After `dotnet pack`, run:

```bash
nugetready check --artifacts ./artifacts/packages
```

The default configuration is `nugetready.json` in the current directory. It declares each package ID, kind, version, exact primary `.nupkg`, optional `.snupkg`, and tool smoke command. An optional `workflowPolicy.expectedNuGetUsername` requires one configured literal publisher username; otherwise the checker accepts any non-empty literal `NuGet/login@v1` username. NuGetReady rejects missing, extra, ambiguous, or filename/version-mismatched artifacts, inspects their metadata and layout, and rehearses them through an isolated local feed and fresh package caches. Reports use the stable states `pass`, `warn`, `fail`, `error`, `not-run`, and `not-applicable`; a blocked check never appears as `pass`. Workflow input is parsed before release relevance filtering, so malformed or incomplete YAML produces `error`/exit `2` with a safe relative workflow location; `not-applicable` means successfully inspected input was proven unrelated.

Archive inspection is bounded to 4,096 entries, 32 MiB expanded per entry, and 256 MiB expanded in aggregate. Limits are enforced while reading and package provenance is compared using streaming operations. This is resource protection for inspection, not a malware sandbox.

## Supported Workflow Profile

Workflow-policy `pass` applies only to the documented closed-world release profile. The triggering tag must be the exact literal `v` plus the configured version, and the configured artifact filenames, packed nuspec identity, immutable validated artifact, and exact primary package selected for publication must all carry that same identity. The checker evaluates jobs with publication operations or recognized credential capability and every dependency or artifact producer that can influence them. A static tag filter alone does not create publication reachability; a reachable publishing workflow must still use the exact release tag. Credential capability includes root `secrets` context references inside GitHub expressions; reusable-workflow `secrets` bindings; complete recognized members of `vars.*`, `inputs.*`, and `env.*`; and recognized keys in workflow/job/step `env` or action/reusable-workflow `with` maps. For the bounded names `NUGET_API_KEY`, `API_KEY`, `ACCESS_TOKEN`, `AUTHORIZATION`, `PASSWORD`, `SECRET`, and `CREDENTIAL`, every non-alphanumeric character is removed and the remaining token is compared case-insensitively by exact equality. Prefixes and suffixes such as `ApiKeyPath` remain outside. A `vars`, `inputs`, or `env` member selector that is not a complete static member name is unresolvable and enters the capability boundary; unknown behavior there blocks as unsupported/unproven. The bounded expression-evaluated grammar follows GitHub Actions' Context availability table: workflow `run-name`, `concurrency`, and `env` values; reusable-workflow input defaults and output values; job `name`, `concurrency`, `container`, `continue-on-error`, `defaults.run`, `env`, `environment`, `if`, outputs, `runs-on`, secrets, services, strategy, `timeout-minutes`, and reusable-workflow inputs; and step `name`, `run`, `if`, `shell`, `working-directory`, `timeout-minutes`, `continue-on-error`, `env`, and action inputs, including local composite-action steps. Every scalar leaf in those named mappings and objects is traversed. Incomplete framing in those fields blocks as `error` with exit code `2`; non-evaluated literals such as workflow `name`, trigger filters, workflow-level `defaults`, and unrelated static configuration do not enter policy merely because they contain expression-like text. Ordinary step-based jobs apply workflow, job, and step environment overrides before evaluating each active step. Literal paths or prose containing `secrets` do not imply the context. Unrelated read-only CI without one of those bindings stays outside release policy.

Runtime environment identity follows the certified Ubuntu target: inheritance and overrides preserve case-sensitive names, and the supported bindings are exactly `KEELMATRIX_NO_TELEMETRY`, `DOTNET_CLI_TELEMETRY_OPTOUT`, `NUGET_PACKAGES`, and the publish-step `NUGET_API_KEY` where applicable. Credential-name normalization is a separate heuristic and does not establish a process binding. The supported producer, acquisition, and validator commands use a strict PowerShell token model that preserves command position, quote delimiters, literal argument bytes, empty arguments, and operators; quoted command heads, path whitespace, grouping, punctuation, escaping, and other unmodeled forms are rejected rather than normalized. Workflow input is parsed before release relevance filtering, so malformed YAML, duplicate keys, invalid roots, and multiple documents produce `error`/exit `2` with a safe relative workflow location; `not-applicable` requires successfully inspected non-applicability.

See the canonical [Supported Release Workflow Profile](https://github.com/KeelMatrix/NuGetReady/blob/main/README.md#supported-release-workflow-profile) for the complete specification and fail-closed result mapping.

## Package-Execution Safety

Consumer rehearsal is not a sandbox. Package code, build assets, and tool smoke commands may execute with the caller's permissions. Use trusted package inputs and an appropriate account. NuGetReady itself does not publish packages and its configuration contains no publishing credentials.

## Privacy

NuGetReady uses `KeelMatrix.Telemetry` for shared activation and at-most-weekly heartbeat signals after a trustworthy completed rehearsal. It does not send package IDs, dependency names, repository identity, package contents, source paths, workflow content, failure logs, or configuration content. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out.

For configuration, check contracts, and troubleshooting, see the [repository README](https://github.com/KeelMatrix/NuGetReady/blob/main/README.md). See [PRIVACY.md](https://github.com/KeelMatrix/NuGetReady/blob/main/PRIVACY.md) for the data boundary and [SECURITY.md](https://github.com/KeelMatrix/NuGetReady/blob/main/SECURITY.md) for security reporting and the execution boundary.
