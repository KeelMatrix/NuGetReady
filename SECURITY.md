# Security Policy

## Reporting a Vulnerability

Please report vulnerabilities privately through [GitHub Security Advisories](https://github.com/KeelMatrix/NuGetReady/security/advisories/new). Do not include secrets or sensitive package contents in a public issue.

## Supported Versions

The supported version is the latest released version. Development builds are not supported release targets.

NuGetReady inspects package archives supplied by the user. Library rehearsals restore and build package assets; tool rehearsals install and execute the configured smoke command. This is not a sandbox: package code and build assets run with the caller's permissions. Use trusted inputs and an appropriate account when running a rehearsal. Temporary feeds, caches, and consumer projects are isolated and bounded, and captured diagnostics are size-limited.
