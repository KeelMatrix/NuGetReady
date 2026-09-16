# KeelMatrix NuGetReady

## Product contract

NuGetReady rehearses the release contract represented by the exact package artifacts in a chosen directory. The `check` command validates the declared artifact set and inspects package metadata, layout, dependency target frameworks, and sensitive archive entries.

The default repository configuration is `nugetready.json` at the repository root. It uses schema version `1` and declares each package ID, package kind, version, and exact `.nupkg`/`.snupkg` filenames.

The command is:

```text
nugetready check [--config <path>] [--artifacts <path>] [--format text|json] [--timeout <duration>]
```

Exit code `0` means the inspection completed without a blocking readiness failure. Exit code `1` means the inspection completed and found a blocking readiness failure. Exit code `2` means input, configuration, package parsing, or infrastructure prevented a trustworthy result.

The current release does not execute package code, perform clean-consumer restore/install rehearsals, inspect release workflows, publish packages, or send telemetry.
