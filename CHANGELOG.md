# Changelog

## [Unreleased]

### Added

- Includes opt-out-aware `KeelMatrix.Telemetry` 0.1.0 activation and at-most-weekly heartbeat requests after trustworthy completed release rehearsals, with shared-contract delivery and KeelMatrix development/CI suppression.

### Fixed

- Classifies unavailable restore sources and related tooling infrastructure as exit-code-2 errors while preserving exit code 1 for reproducibly unready package assets, including bad-image diagnostics.
- Prevents inherited fallback folders and shared-cache substitutions from satisfying consumer rehearsals, verifies restored package provenance, and supports declaration-order-independent public dependency graphs.
- Validates portable PDBs, matching symbol layouts, file-based licenses, deliberate archive-sensitive paths, bounded process cleanup, safe deterministic diagnostics, truthful skipped-check states, and JSON-formatted CLI errors.
- Makes workflow policy checks structural and job-scoped, publishes the package and adjacent symbols once, and closes package inspection and pack-time guards against unexpected or local-sensitive content.

## [0.1.0] - 2026-09-16

### Added

- Provides deterministic exact-artifact and package-archive readiness checks for NuGet libraries and .NET tools.
- Rehearses isolated library restore/build against a public API for every declared target framework and .NET tool installation/smoke execution using the just-built artifacts, a temporary local feed, controlled sources, and a fresh package cache.
- Supports versioned repository configuration, text and JSON reports, stable exit codes, and narrow release-workflow policy checks.
