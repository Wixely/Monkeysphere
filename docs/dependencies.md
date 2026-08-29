# Dependency policy

- Direct versions are pinned centrally in `Directory.Packages.props`.
- Prefer MIT or Apache-2.0 dependencies and review direct, transitive, bundled, and browser-asset licensing before adoption.
- Required browser assets are repository-local; public CDNs are not part of the runtime path.
- DnaX packages are built from the exact public release tag documented in `THIRD-PARTY-NOTICES.md`. Update them only by reviewing the new tag, rebuilding in a clean worktree, replacing all affected packages, updating hashes/notices, and running the full migration and remote-access suites.
- Durable release files belong in GitHub Releases. The release workflow publishes its ZIP and checksums directly and does not use workflow artifacts. Any future GitHub Actions artifact upload must set an explicit short `retention-days`.
- SkiaSharp and its dependency-free Linux native assets are pinned to `4.151.1` for bounded image decoding and metadata-free WebP derivative generation.

`eng\VerifySupplyChain.ps1` enforces the recorded hashes for every vendored DnaX package and browser asset, checks that those hashes remain in `THIRD-PARTY-NOTICES.md`, rejects per-project NuGet version overrides, and rejects public HTTP dependencies in first-party browser entry points. `eng\Build.ps1` runs the offline checks before restore. Run `eng\VerifySupplyChain.ps1 -AuditVulnerabilities` with network access before a release to query the configured NuGet sources for current direct and transitive advisories.

The complete direct and transitive NuGet inventory was reviewed on 2026-08-29. The runtime graph is composed of the pinned DnaX, Dapper, Microsoft .NET/ASP.NET Core, Model Context Protocol C# SDK, SQLitePCLRaw/SQLite, and SkiaSharp families described in `THIRD-PARTY-NOTICES.md`; the test-only graph additionally contains Microsoft Test Platform, xUnit.net, and coverlet packages. The online NuGet advisory audit reported no known vulnerable package on that date. This is time-sensitive evidence and must be regenerated for a release.
