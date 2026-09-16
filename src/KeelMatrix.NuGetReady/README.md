# KeelMatrix.NuGetReady

NuGetReady is a .NET tool that rehearses a NuGet release from the exact package artifacts you built before you publish them.

## Install

```bash
dotnet tool install --global KeelMatrix.NuGetReady
```

## Quick Start

After `dotnet pack`, run:

```bash
nugetready check --artifacts ./artifacts/packages
```

The check validates the declared `.nupkg` and `.snupkg` set, inspects archive metadata and layout, and uses an isolated local feed and package cache. Library consumers are restored and built against a public type from every declared `lib/` or `ref/` target framework; runnable target frameworks also execute the generated consumer. .NET tools are installed and run with their configured smoke arguments.

## Limitations and safety

NuGetReady performs narrow structural workflow checks; it does not publish packages, create releases, or prove arbitrary GitHub Actions behavior. The consumer rehearsal is not a sandbox: package code and build assets may execute with the caller's permissions. Use trusted package inputs and an appropriate account.

NuGetReady itself does not send telemetry. See the [privacy policy](https://github.com/KeelMatrix/NuGetReady/blob/main/PRIVACY.md) and [security policy](https://github.com/KeelMatrix/NuGetReady/blob/main/SECURITY.md) for the data and execution boundaries.

See the [repository README](https://github.com/KeelMatrix/NuGetReady/blob/main/README.md) for the configuration schema, troubleshooting, and complete check-family documentation.
