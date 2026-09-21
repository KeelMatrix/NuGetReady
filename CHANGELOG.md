# Changelog

## [Unreleased]

The 0.1.0 release is planned and unreleased.

### Added

- Provides deterministic exact-artifact and package-archive readiness checks for NuGet libraries and .NET tools.
- Rehearses isolated library restore/build against a public API for every declared target framework and .NET tool installation/smoke execution using the just-built artifacts, a temporary local feed, controlled sources, and a fresh package cache.
- Supports versioned repository configuration, text and JSON reports, stable exit codes, narrow release-workflow policy checks, and explicit `not-run`/`not-applicable` states when a check cannot be evaluated.
- Validates portable PDB identity and checksum correspondence, package-sensitive paths, file-based licenses, bounded process cleanup, deterministic diagnostics, and telemetry opt-out behavior.
- Rejects manifest-defined sensitive-name families when protected names are extended or used as directory segments, while boundary-aware fragment matching accepts legitimate assemblies that merely contain protected tokens.
- Requires exactly one primary archive per package expectation, rejects duplicate package identities, and keeps optional symbol association unambiguous.
- Verifies the actual NuGet package-version cache `.nupkg`/`.nupkg.sha512` provenance while comparing the full expanded payload, including XML documentation.
- Establishes Unix process groups before target execution and confirms bounded descendant cleanup after timeout or cancellation; Windows retains job-object cleanup.
