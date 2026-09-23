# Privacy

NuGetReady performs archive inspection and a local consumer rehearsal. It may contact the configured public NuGet source when a declared public dependency needs to restore, but it does not upload package IDs, package contents, source paths, repository identity, or reports. The temporary feed, package cache, HTTP cache, and consumer project are removed after the rehearsal where the host permits cleanup.

## Telemetry

NuGetReady depends on `KeelMatrix.Telemetry` `[0.1.1]` and requests shared activation and heartbeat events only after a complete rehearsal returns a trustworthy pass, warning, or readiness failure. Installation, assembly loading, process start, malformed input, and infrastructure failures do not activate telemetry. The shared client is fire-and-forget, best-effort, and emits at most one heartbeat per consuming project per ISO week.

`KeelMatrix.Telemetry` 0.1.1 is the canonical source for the event fields, opt-out precedence, endpoint, local storage, and retention. Its shared envelope contains the event type, tool name, tool version, telemetry version, schema version, a stable pseudonymous consuming-codebase hash, and a stable pseudonymous installation hash. Activation adds runtime, operating-system, CI, and UTC timestamp fields; a heartbeat adds its ISO week. These pseudonymous identifiers are not described here as anonymous or unlinkable.

NuGetReady passes no package IDs, dependency names, repository URLs or raw repository identity, owners, specific file names, package contents, workflow text, source paths, failure logs, configuration content, or other rehearsal data to the shared client. It does not add product-specific telemetry fields. The shared dependency may derive its pseudonymous identifiers from its documented inputs and may write its own local queue and marker state; see the [shared README](https://github.com/KeelMatrix/Telemetry#readme) and [shared privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md) for those implementation details.

Consumers can opt out for the current process using the established environment mechanism:

```powershell
$env:KEELMATRIX_NO_TELEMETRY="1"
```

`KeelMatrix.Telemetry` also honors `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DO_NOT_TRACK=1`, and its documented repository-local opt-out files. This repository sets the opt-out for development tests, fixture runs, package smoke, and CI so synthetic validation is not treated as product demand. Telemetry failures never change the report, exit code, offline behavior, or bounded rehearsal behavior.
