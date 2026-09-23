# Changelog

## [Unreleased]

The 0.1.0 release is planned and unreleased.

### Added

- Provides deterministic exact-artifact and package-archive readiness checks for NuGet libraries and .NET tools.
- Rehearses isolated library restore/build against a public API for every declared target framework and .NET tool installation/smoke execution using the just-built artifacts, a temporary local feed, controlled sources, and a fresh package cache.
- Supports versioned repository configuration, text and JSON reports, stable exit codes, narrow release-workflow policy checks, and explicit `not-run`/`not-applicable` states when a check cannot be evaluated.
- Validates portable PDB identity and checksum correspondence, package-sensitive paths, file-based licenses, bounded process cleanup, deterministic diagnostics, and telemetry opt-out behavior.
- Rejects manifest-defined sensitive-name families when protected names are extended or used as directory segments, while the separate package-content contract rejects undeclared binary and XML entries even when their names are legitimate assemblies.
- Requires exactly one primary archive per package expectation, rejects duplicate package identities, and keeps optional symbol association unambiguous.
- Verifies library package-cache and installed-tool-store identity/provenance and compares the full expanded payload, including XML documentation; unsupported tool layouts are reported as unproven rather than passing.
- Requires the package-specific provenance sidecar and blocks unsupported reusable/composite publication paths as unproven rather than treating them as release proof.
- Defines a closed-world release-workflow profile with a real YAML representation model, an isolated validation/artifact job, an exact artifact dependency and handoff, and a minimal Trusted Publishing job containing only download, login, and one direct exact push.
- Makes unknown or unclassified publication-relevant YAML, expression, action, shell, command, MSBuild, credential, permission, dependency, or artifact semantics a blocking unsupported error; deterministic violations of the understood profile remain readiness failures.
- Proves effective permission and environment overrides, authentication order, temporary credential scope, exact artifact continuity, and version-tag-only reachability, while blocking reusable workflows, composite actions, aliases/merge keys, wrappers, generated scripts, dynamic command resolution, and other unmodeled execution mechanisms.
- Establishes Unix process groups before target execution and confirms bounded descendant cleanup after successful completion, timeout, or cancellation; Windows retains job-object cleanup.
