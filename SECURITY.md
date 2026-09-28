# Security Policy

## Reporting a Vulnerability

Please report vulnerabilities privately through [GitHub Security Advisories](https://github.com/KeelMatrix/NuGetReady/security/advisories/new). Do not include secrets or sensitive package contents in a public issue.

## Supported Versions

The supported version is the latest released version. Development builds are not supported release targets.

NuGetReady inspects package archives supplied by the user. Before archive metadata, layout, dependency, or sensitive-path decisions, it rejects non-canonical and colliding archive-entry paths, including rooted paths, dot segments, separator aliases, case collisions, and Unicode-normalization collisions. Artifact roots and every ancestor are identity-checked without following reparse points; source archive bytes are copied only after the opened path identity matches the accepted tree, and root/parent rebinding fails closed before archive inspection. Library rehearsals restore and build package assets; tool rehearsals install and execute only a configured single-name executable child of the isolated tool directory. This is not a sandbox: package code and build assets run with the caller's permissions. Use trusted inputs and an appropriate account when running a rehearsal. Artifact-tree traversal, archive counts and sizes, temporary feeds, caches, and consumer projects are bounded and isolated, and captured diagnostics are size-limited.
