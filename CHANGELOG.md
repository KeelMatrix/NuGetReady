# Changelog

This changelog records user-visible changes to KeelMatrix.NuGetReady.

## [Unreleased]

## [0.1.0] - 2026-10-05

### Added

- Validates explicitly declared NuGet package and symbol archives for package identity, version, metadata, dependencies, target-framework groups, symbols, and unexpected or sensitive content.
- Rehearses library consumption and .NET tool installation from the exact built artifacts with controlled package sources and isolated caches, including supported consumer restore, build, and configured smoke execution.
- Evaluates a documented, narrow release-workflow profile for tag, version, artifact, and publishing identity; unsupported workflow behavior is reported as unproven rather than passed.
- Produces deterministic text or versioned JSON reports with stable exit codes and bounded diagnostics.
