# Privacy

NuGetReady performs archive inspection and a local consumer rehearsal. It may contact the configured public NuGet source when a declared public dependency needs to restore, but it does not upload package IDs, package contents, source paths, repository identity, or reports. The temporary feed, package cache, HTTP cache, and consumer project are removed after the rehearsal where the host permits cleanup.

## Telemetry

NuGetReady depends on `KeelMatrix.Telemetry` and requests anonymous activation and heartbeat events only after a complete rehearsal returns a trustworthy pass or readiness failure. Installation, assembly loading, process start, malformed input, and infrastructure failures do not activate telemetry. The shared client is non-blocking, best-effort, and limits heartbeats to at most once per project per ISO week.

NuGetReady derives only bounded product context: product version, package-count bucket, artifact-kind distribution, check-count bucket, pass/fail outcome, broad local/CI class, and coarse duration bucket. It never supplies package IDs, dependency names, repository URLs or identity, owners, specific file names, package contents, workflow text, source paths, failure logs, or configuration content to the telemetry client. The shared dependency owns the anonymous activation/heartbeat envelope and durable delivery; NuGetReady does not add arbitrary events or payload fields.

Telemetry is disabled for KeelMatrix development and CI. Consumers can opt out for the current process using the established environment mechanism:

```powershell
$env:KEELMATRIX_NO_TELEMETRY="1"
```

`KeelMatrix.Telemetry` also honors `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DO_NOT_TRACK=1`, and its documented repository-local opt-out files. The shared package's [README](https://github.com/KeelMatrix/Telemetry#readme) and [privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md) are the source of truth for its common anonymous envelope, local queue, delivery endpoint, and retention. Telemetry failures never change the report, exit code, or bounded rehearsal behavior.
