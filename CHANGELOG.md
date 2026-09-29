# Changelog

This changelog records user-visible changes to KeelMatrix.NuGetReady.

## [Unreleased]

The 0.1.0 release is planned and unreleased.

### Added

- Provides deterministic exact-artifact and package-archive readiness checks for NuGet libraries and .NET tools.
- Rehearses isolated library restore/build against a public API for every declared target framework and .NET tool installation/smoke execution on Windows using the just-built artifacts, a temporary local feed, controlled sources, and a fresh package cache; Linux and macOS fail closed before tool smoke execution with an infrastructure error identifying the configured smoke command when a handle-bound launch cannot be provided.
- Supports versioned repository configuration, text and JSON reports, stable exit codes, narrow release-workflow policy checks, and explicit `not-run`/`not-applicable` states when a check cannot be evaluated.
- Validates portable PDB identity and checksum correspondence, package-sensitive paths, file-based licenses, bounded process cleanup, deterministic diagnostics, and telemetry opt-out behavior.
- Rejects manifest-defined sensitive-name families when protected names are extended or used as directory segments, while the separate package-content contract rejects undeclared binary and XML entries even when their names are legitimate assemblies.
- Requires exactly one primary archive per package expectation, rejects duplicate package identities, and keeps optional symbol association unambiguous.
- Verifies library package-cache and installed-tool-store identity/provenance and compares the full expanded payload, including XML documentation; unsupported tool layouts are reported as unproven rather than passing.
- Requires the package-specific provenance sidecar and blocks unsupported reusable/composite publication paths as unproven rather than treating them as release proof.
- Defines a closed-world release-workflow profile that binds one exact version tag to the configured package version, versioned artifact filenames, inspected nuspec identity, immutable validation artifact, and exact primary package selected for publication.
- Limits workflow-policy evaluation to nodes with publication capability or operations and to dependency or artifact paths that can influence publication; static tag filters and unknown execution in unrelated read-only CI without a recognized credential binding remain outside release policy, while a reachable publishing workflow still requires the exact configured-version tag.
- Blocks unknown or unclassified YAML, expression, action, shell, command, MSBuild, credential, permission, dependency, or artifact semantics inside the reachable publication boundary as unsupported/unproven; malformed/incomplete expression framing blocks across the bounded GitHub-evaluated workflow, reusable-workflow, job, service/container, step, and composite-action grammar, while non-evaluated literal data remains outside that classification and deterministic violations of the understood profile remain readiness failures.
- Proves effective permission and per-active-step environment overrides with case-sensitive Ubuntu runtime environment identity; treats root `secrets` context references, reusable-workflow secret bindings, bounded credential names after removing every non-alphanumeric character and case-folding, and matching static `vars`, `inputs`, or `env` members as credential-bearing without substring matching or classifying literal `secrets` prose or paths. Credential-name heuristics remain separate from runtime binding proof; unresolvable member selectors, malformed workflow input, and duplicate/invalid YAML structures block as input errors, while exact PowerShell command templates preserve quoting, argument bytes, empty arguments, and operators and reject unmodeled forms. The release path verifies root configuration identity, authentication order, temporary credential scope, and exact artifact continuity while blocking unmodeled execution mechanisms.
- Establishes Unix process groups before target execution and confirms bounded descendant cleanup after successful completion, timeout, or cancellation; Windows retains job-object cleanup.
- Applies an explicit consumer-framework and SDK/tooling matrix, reporting unsupported or unavailable rehearsal infrastructure as error/exit 2 and identifying build-only validation as non-execution evidence.
- Bounds archive inspection to a fixed entry count and expanded-size budget, verifies package provenance with bounded streaming operations, and reports unproven process cleanup as error/exit 2.
- Bounds artifact-tree discovery before archive work, rejects reparse-point and containment escapes, and requires a non-overlapping candidate source mapping for the release validator.
- Revalidates the entire bounded artifact tree and archive hashes after enumeration, failing closed on post-scan additions, replacements, deletions, limit bypasses, or parent reparse-point rebinding.
- Rejects duplicate, aliased, rooted, traversal, case-colliding, and Unicode-normalization-colliding archive entry paths before package metadata or layout inspection.
- Accepts customer-owned Trusted Publishing usernames in the supported workflow profile, with an optional explicit expected username for repositories that require one.
- Snapshots counted package archives before inspection, uses one immutable snapshot for archive, dependency, feed, consumer, and provenance checks, and verifies the source files remain unchanged after the rehearsal.
- Rejects tool smoke arguments outside `dotnetTool` expectations or containing embedded NUL characters, isolates generated consumers from ambient `Directory.Build.*` imports, escapes control characters in human-readable diagnostics, and inspects the exact release-built package bytes before release upload.
- Inspects workflow, configuration, local script, and composite-action paths through a pinned repository identity snapshot and descriptor-relative handles; missing pinned nodes, reparse points, and root, ancestor, or leaf rebinding fail closed without reading outside content.

### Fixed

- Pins the accepted artifact root and traversed ancestors for handle-relative enumeration, attribute inspection, archive opens, hashing, snapshot copies, and final verification, failing closed before outside-root bytes are read during rebinds.
- Keeps tool-command validation bound to a direct non-reparse installed child and, on Windows, launches a private handle-held snapshot of the complete installed tool directory, closing apphost and dependency replacement windows through process creation. Linux and macOS return an infrastructure error identifying the configured smoke command when their generic process-creation APIs cannot provide the same binding.
- Aligns CI validation with the platform contract: Windows requires a successful smoke rehearsal, while Linux and macOS accept only the documented configured-smoke-command launch error with exit code `2`.
- Treats deletion of any pinned workflow-policy node after repository snapshot as an unsafe input instead of allowing the workflow check to become `not-applicable`.
