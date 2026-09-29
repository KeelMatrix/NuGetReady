# Security Policy

## Reporting a Vulnerability

Public cancellation returns `error`/exit `2`, marks downstream checks `not-run`, and does not emit telemetry. Library consumer projects use an isolated SDK/import boundary that excludes ambient `Directory.Build.*`, custom before/after targets, and user-extension imports while retaining package build assets.

Please report vulnerabilities privately through [GitHub Security Advisories](https://github.com/KeelMatrix/NuGetReady/security/advisories/new). Do not include secrets or sensitive package contents in a public issue.

## Supported Versions

The supported version is the latest released version. Development builds are not supported release targets.

NuGetReady inspects package archives supplied by the user. Before archive metadata, layout, dependency, or sensitive-path decisions, it rejects non-canonical and colliding archive-entry paths, including rooted paths, dot segments, separator aliases, case collisions, and Unicode-normalization collisions. The accepted artifact root and every traversed ancestor are pinned without following reparse points; enumeration, attribute inspection, archive opening, hashing, snapshot copying, and final verification are handle-relative, so root/parent rebinding fails closed before outside-root bytes are read. Workflow-policy inspection uses a pinned repository identity snapshot and descriptor-relative reads for workflow, configuration, script, and composite-action paths; disappearance of a pinned node, reparse points, and root, ancestor, or leaf rebinding fail closed without reading outside content. Library rehearsals restore and build package assets. Tool rehearsals validate a configured single-name executable child against installed metadata, copy the complete payload into a private content-bound launch image, and revalidate that image immediately before process creation; Windows additionally holds its entries with replacement-blocking sharing. This is not a sandbox: package code and build assets run with the caller's permissions. Use trusted inputs and an appropriate account when running a rehearsal. Artifact-tree traversal, archive counts and sizes, temporary feeds, and consumer projects are bounded and isolated, and captured diagnostics are size-limited.

Pack-time sensitive-input checks exclude only `bin`/`obj` container segments from generated source provenance; protected source filenames and families, linked and renamed sources, wildcard-selected inputs, and all package destinations remain subject to the policy before archive creation.
