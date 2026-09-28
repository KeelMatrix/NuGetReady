# Security Policy

## Reporting a Vulnerability

Please report vulnerabilities privately through [GitHub Security Advisories](https://github.com/KeelMatrix/NuGetReady/security/advisories/new). Do not include secrets or sensitive package contents in a public issue.

## Supported Versions

The supported version is the latest released version. Development builds are not supported release targets.

NuGetReady inspects package archives supplied by the user. Before archive metadata, layout, dependency, or sensitive-path decisions, it rejects non-canonical and colliding archive-entry paths, including rooted paths, dot segments, separator aliases, case collisions, and Unicode-normalization collisions. The accepted artifact root and every traversed ancestor are pinned without following reparse points; enumeration, attribute inspection, archive opening, hashing, snapshot copying, and final verification are handle-relative, so root/parent rebinding fails closed before outside-root bytes are read. Library rehearsals restore and build package assets; tool rehearsals validate a configured single-name executable child against installed metadata, then execute a private launch snapshot copied from pinned handles. This is not a sandbox: package code and build assets run with the caller's permissions. Use trusted inputs and an appropriate account when running a rehearsal. Artifact-tree traversal, archive counts and sizes, temporary feeds, caches, and consumer projects are bounded and isolated, and captured diagnostics are size-limited.
