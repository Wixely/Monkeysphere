# Release process

Monkeysphere release packages are framework-dependent .NET 10 deployments for Windows x64 and Linux x64. Server-side Blazor is deliberately not AOT-compiled; the target machine must have the ASP.NET Core 10 runtime, while the Docker image supplies that runtime itself.

## Local release candidate

From Windows PowerShell 5.1:

```powershell
.\eng\Build.ps1
.\eng\VerifySupplyChain.ps1 -AuditVulnerabilities
.\eng\PackageRelease.ps1 -Version 0.1.0-rc.1
```

The two RID-specific packages and `SHA256SUMS.txt` are written beneath `.artifacts\releases\<version>`. Packaging fails if that version directory already exists, so a previous candidate is never silently overwritten. Each ZIP contains the published web host, deployment templates, operational/security documentation, MIT license, and third-party notices. Debug symbols are removed so local source paths are not distributed. Packages do not contain a database, credentials, keys, logs, or user media.

Verify the ZIP on a clean Windows machine and a clean Linux machine before treating it as portable release evidence. The local package command proves deterministic inputs and layout, not cross-platform execution.

Use the repository's [deployment verification](deployment-verification.md) scripts for repeatable package, service, systemd-unit, and Docker checks. Record the exact commit and environment; do not convert a static check or an unexecuted CI job into a platform-support claim.

## GitHub automation

The verification workflow runs locked restore, Release build, and tests on Windows and Linux. The Windows leg additionally enforces vendored hashes, central NuGet versions, notices, and the no-CDN browser boundary. Workflows check out Git directly and use only shell and .NET commands; they do not add Node.js or Python actions/tooling to the project.

Pushing a `v*` tag runs the complete Windows suite, performs a current NuGet vulnerability audit, builds the Windows x64 and Linux x64 ZIPs, generates SHA-256 evidence, and publishes all three files directly to a durable GitHub Release using the runner's GitHub CLI. No workflow artifact is uploaded or retained. A failed test, audit, package operation, existing release, or missing tag stops publication.

Before pushing a release tag, complete the repository's required outgoing-history privacy review, confirm the Wixely author/committer identity, update dated verification evidence, and verify any platform support being claimed. Creating or pushing a tag remains a deliberate operator action; the repository contains no remote today and the workflows do not publish anything by themselves.
