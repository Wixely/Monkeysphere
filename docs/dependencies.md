# Dependency policy

- Direct versions are pinned centrally in `Directory.Packages.props`.
- Prefer MIT or Apache-2.0 dependencies and review direct, transitive, bundled, and browser-asset licensing before adoption.
- Required browser assets are repository-local; public CDNs are not part of the runtime path.
- DnaX packages are built from the exact public release tag documented in `THIRD-PARTY-NOTICES.md`. Update them only by reviewing the new tag, rebuilding in a clean worktree, replacing all affected packages, updating hashes/notices, and running the full migration and remote-access suites.
- Durable release files belong in GitHub Releases. Any future GitHub Actions artifact upload must set an explicit short `retention-days`.
