# Deployment verification

Last updated: 2026-08-29

These checks exercise published deployment artifacts rather than the test host. They use isolated ports and temporary data roots. The scripts verify readiness, the default administrator login, authenticated setup rendering, creation of both DnaX-managed SQLite databases, restart behavior, and persistence of the data root.

## Windows package

From Windows PowerShell 5.1, package a candidate and exercise its published executable twice against the same temporary data root:

```powershell
.\eng\PackageRelease.ps1 -Version local-verification
.\eng\VerifyWindowsPackage.ps1 -PackagePath .\.artifacts\releases\local-verification\monkeysphere-local-verification-win-x64.zip
```

The verifier owns and removes only a randomly named directory beneath the current user's temporary directory. Pass `-KeepWorkingDirectory` to retain it for diagnosis.

## Windows Service

Service verification changes the host temporarily and therefore requires an elevated Windows PowerShell 5.1 session plus the explicit authorization switch:

```powershell
.\eng\VerifyWindowsService.ps1 `
    -PackagePath .\.artifacts\releases\local-verification\monkeysphere-local-verification-win-x64.zip `
    -AuthorizeServiceChanges
```

The script refuses to replace an existing service. It creates a uniquely named manual-start service, starts it, signs in, stops it, checks the databases, starts and signs in again, then deletes only the service it created. If Windows cannot confirm deletion, the working directory is retained so the registered command never points at removed files.

## Linux interactive and systemd unit

On a Linux x64 host with the ASP.NET Core 10 runtime, `curl`, and POSIX shell tools:

```sh
dotnet publish src/Monkeysphere.Web/Monkeysphere.Web.csproj \
    --configuration Release --runtime linux-x64 --self-contained false \
    --output .artifacts/linux-smoke
./eng/VerifyLinuxPackage.sh .artifacts/linux-smoke
./eng/VerifySystemdUnit.sh
```

The first script launches the published host twice against one temporary data root. The second uses `systemd-analyze verify` with temporary, existing paths and the current account to validate the packaged unit's syntax, command shape, and baseline hardening directives without installing it.

When a per-user systemd manager is running, the non-privileged transient lifecycle check additionally verifies `Type=notify`, start, authenticated access, stop, restart, and data-root persistence:

```sh
./eng/VerifySystemdLifecycle.sh .artifacts/linux-smoke
```

Passing these checks does not prove installation or boot enablement of the packaged system unit. Before claiming complete systemd support, provision the documented `monkeysphere` account and paths on a disposable Linux host, install the unit, and verify enablement, boot startup, upgrade, and failure recovery.

## Docker

On a host with Docker Engine, the Compose plugin, `curl`, and POSIX shell tools:

```sh
./eng/VerifyDocker.sh
```

The script assigns a scoped Compose project name and port, validates the resolved Compose model, builds and starts the image, signs in, verifies both databases inside the named volume, restarts the container, repeats the checks, and removes only that project's containers, network, and volume. `MONKEYSPHERE_VERIFY_PORT` can select another port. A custom `MONKEYSPHERE_VERIFY_PROJECT` must retain the `monkeysphere-verify-` prefix and contain only safe project-name characters.

The underlying OCI image and volume behavior can also be checked independently with Docker or rootless Podman:

```sh
CONTAINER_ENGINE=podman ./eng/VerifyOciContainer.sh
```

This builds the same Dockerfile, verifies that the runtime uses non-root UID 1654, signs in, checks both databases, recreates the container against the same named volume, and repeats the checks. It does not validate Compose interpolation or Docker-specific engine behavior when run with Podman.

On a rootless Podman host with the pinned `docker:28-dind` image available, Docker Engine and Compose can be exercised without installing a host daemon:

```sh
./eng/VerifyNestedDocker.sh
```

This creates one privileged container inside the caller's rootless user namespace, starts an isolated Docker daemon using ephemeral VFS storage, adds `curl` to that disposable Alpine environment, and runs `VerifyDocker.sh` inside it. The repository is mounted read-only and the outer container is removed on exit. This validates Docker and Compose behavior, but not host boot integration or a production host's storage/network policy.

## Continuous verification

The verification workflow performs the Windows package check, Linux interactive check, static systemd-unit check, and Docker check on GitHub-hosted runners. This workflow is executable release evidence only after it runs successfully for the exact commit being considered; the presence of the workflow alone is not evidence that those platforms passed.
