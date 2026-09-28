## Navigation

- `src/KeelMatrix.NuGetReady` contains the `nugetready` executable, configuration, archive inspection, artifact checks, and report formatting.
- `tests/KeelMatrix.NuGetReady.Tests` contains contract and fixture-based tests for the CLI and package checks.
- `nugetready.json` is the repository's version-1 example configuration and declares the exact package archives produced by a Release pack.

## Commands

```text
dotnet restore KeelMatrix.NuGetReady.sln --configfile NuGet.config
dotnet build KeelMatrix.NuGetReady.sln -c Release --no-restore
dotnet test KeelMatrix.NuGetReady.sln -c Release --no-build --no-restore
dotnet pack src/KeelMatrix.NuGetReady/KeelMatrix.NuGetReady.csproj -c Release --no-build --no-restore --include-symbols -p:SymbolPackageFormat=snupkg -o artifacts/packages
dotnet run --project src/KeelMatrix.NuGetReady -- check --artifacts artifacts/packages
```

## Invariants

- The shipping project is a `net8.0` .NET tool with command `nugetready` and package version `0.1.0` for this milestone.
- Configuration schema version is `1`; expected package artifacts are explicit filenames, never inferred from a wildcard.
- Exit code `0` is a trustworthy pass, `1` is a completed readiness failure, and `2` is invalid input or an infrastructure failure.
- Reports contain stable ordering and no timestamps or machine-specific paths.
- Artifact scans are reverified against the counted tree and archive hashes before any downstream inspection; ancestor/root/file identities are checked without following reparse points, source bytes are copied only after the opened identity matches the accepted tree, and additions, replacements, deletions, limit bypasses, and reparse-point rebinding fail closed.
- Archive entries must be unique canonical relative paths; rooted names, dot segments, separator aliases, case collisions, and Unicode-normalization collisions fail before metadata, layout, or sensitive-path decisions.
- Library rehearsals restore/build package assets; tool rehearsals execute only a configured single-name executable child with bounded processes. The tool does not publish packages or run private CI. Completed trustworthy rehearsals use the required `KeelMatrix.Telemetry` dependency; local development and CI set the established telemetry opt-out.

## Validation

Run the focused test project first, then the Release solution build, package creation, and a check against the packed artifacts. Inspect the actual `.nupkg` and `.snupkg`; do not treat source metadata alone as package evidence.
