# Privacy

NuGetReady performs archive inspection and a local consumer rehearsal. It may contact the configured public NuGet source when a declared public dependency needs to restore, but it does not upload package IDs, package contents, source paths, repository identity, or reports. The temporary feed, package cache, HTTP cache, and consumer project are removed after the rehearsal where the host permits cleanup.

NuGetReady does not include a telemetry dependency. It does not collect or transmit usage data.

Future telemetry, if introduced, will remain minimal, opt-out friendly, and documented before use. Local development and validation will not be treated as production usage.
