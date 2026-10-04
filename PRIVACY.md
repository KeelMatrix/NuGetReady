# Privacy

NuGetReady performs archive inspection and a local consumer rehearsal. It may contact the configured public NuGet source when a declared public dependency needs to restore, but it does not upload package IDs, package contents, source paths, repository identity, or reports. The temporary feed, package cache, HTTP cache, and consumer project are removed after the rehearsal where the host permits cleanup.

## Telemetry

NuGetReady depends on `KeelMatrix.Telemetry` `[0.1.1]` and requests shared activation and heartbeat events only after a complete rehearsal returns a trustworthy pass, warning, or readiness failure. Installation, assembly loading, process start, malformed input, and infrastructure failures do not activate telemetry.

`KeelMatrix.Telemetry` 0.1.1 is the canonical source for the event fields, opt-out resolution, identity derivation, local state, delivery, heartbeat cadence, and retention. Its shared privacy policy documents the fields and identifiers it may emit; see the [shared README](https://github.com/KeelMatrix/Telemetry#readme) and [shared privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md).

NuGetReady calls the shared API with the fixed tool name `nugetready` and the `Program` entry assembly. It adds no rehearsal-specific event fields and supplies no package IDs, dependency names, raw repository URLs, owner names, specific file names, package contents, workflow text, source paths, failure logs, or configuration content. The shared client owns its documented pseudonymous project and installation identifiers; see the shared policy for their derivation and handling.

Consumers can opt out for the current process using the established environment mechanism:

```powershell
$env:KEELMATRIX_NO_TELEMETRY="1"
```

The shared client owns other process and repository-local opt-outs and telemetry failure handling. This repository sets `KEELMATRIX_NO_TELEMETRY=1` for development tests, fixture runs, package smoke, and CI so synthetic validation is not treated as product demand. Customer CI may count when process and repository opt-outs are unset.
