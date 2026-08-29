[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $PackagePath,

    [ValidateRange(1024, 65535)]
    [int] $Port = 15081,

    [switch] $KeepWorkingDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$workingRoot = Join-Path $temporaryParent ("monkeysphere-windows-verify-" + [guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $workingRoot 'package'
$dataRoot = Join-Path $workingRoot 'data'
$process = $null
$previousDataRoot = [Environment]::GetEnvironmentVariable('MONKEYSPHERE_DATA_ROOT', 'Process')
$previousUsername = [Environment]::GetEnvironmentVariable('MONKEYSPHERE_ADMIN_USERNAME', 'Process')
$previousPassword = [Environment]::GetEnvironmentVariable('MONKEYSPHERE_ADMIN_PASSWORD', 'Process')
$previousEnvironment = [Environment]::GetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Process')

function Stop-SmokeProcess {
    if ($null -ne $script:process -and -not $script:process.HasExited) {
        Stop-Process -Id $script:process.Id -Force
        $script:process.WaitForExit(10000) | Out-Null
    }
    $script:process = $null
}

function Start-SmokeProcess([string] $executable) {
    $script:process = Start-Process -FilePath $executable `
        -ArgumentList @('--urls', "http://127.0.0.1:$Port") `
        -WorkingDirectory (Split-Path -Parent $executable) `
        -WindowStyle Hidden `
        -PassThru
}

New-Item -ItemType Directory -Path $packageRoot, $dataRoot | Out-Null
try {
    Expand-Archive -LiteralPath $resolvedPackage -DestinationPath $packageRoot
    $executable = Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter 'Monkeysphere.Web.exe' | Select-Object -First 1
    if ($null -eq $executable) {
        throw 'The package does not contain Monkeysphere.Web.exe.'
    }

    [Environment]::SetEnvironmentVariable('MONKEYSPHERE_DATA_ROOT', $dataRoot, 'Process')
    [Environment]::SetEnvironmentVariable('MONKEYSPHERE_ADMIN_USERNAME', 'admin', 'Process')
    [Environment]::SetEnvironmentVariable('MONKEYSPHERE_ADMIN_PASSWORD', 'admin', 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production', 'Process')

    Start-SmokeProcess $executable.FullName
    & (Join-Path $PSScriptRoot 'TestWebDeployment.ps1') -BaseUri "http://127.0.0.1:$Port"
    Stop-SmokeProcess

    $applicationDatabase = Join-Path $dataRoot 'monkeysphere.db'
    $remoteDatabase = Join-Path $dataRoot 'remote-access.db'
    if (-not (Test-Path -LiteralPath $applicationDatabase -PathType Leaf) -or
        -not (Test-Path -LiteralPath $remoteDatabase -PathType Leaf)) {
        throw 'The packaged host did not create both managed SQLite databases.'
    }

    Start-SmokeProcess $executable.FullName
    & (Join-Path $PSScriptRoot 'TestWebDeployment.ps1') -BaseUri "http://127.0.0.1:$Port"
    Write-Host 'Windows package restart and persistent-data smoke passed.'
}
finally {
    Stop-SmokeProcess
    [Environment]::SetEnvironmentVariable('MONKEYSPHERE_DATA_ROOT', $previousDataRoot, 'Process')
    [Environment]::SetEnvironmentVariable('MONKEYSPHERE_ADMIN_USERNAME', $previousUsername, 'Process')
    [Environment]::SetEnvironmentVariable('MONKEYSPHERE_ADMIN_PASSWORD', $previousPassword, 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', $previousEnvironment, 'Process')

    if ($KeepWorkingDirectory) {
        Write-Host "Verification files retained at $workingRoot"
    }
    else {
        $resolvedWorkingRoot = [IO.Path]::GetFullPath($workingRoot)
        if (-not $resolvedWorkingRoot.StartsWith($temporaryParent, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedWorkingRoot) -notlike 'monkeysphere-windows-verify-*') {
            throw "Refusing to remove unexpected verification path: $resolvedWorkingRoot"
        }
        Remove-Item -LiteralPath $resolvedWorkingRoot -Recurse -Force
    }
}
