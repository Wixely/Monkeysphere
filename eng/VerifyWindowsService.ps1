[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $PackagePath,

    [ValidatePattern('^MonkeysphereVerification[A-Za-z0-9]{4,32}$')]
    [string] $ServiceName = ("MonkeysphereVerification" + [guid]::NewGuid().ToString('N').Substring(0, 8)),

    [ValidateRange(1024, 65535)]
    [int] $Port = 15084,

    [Parameter(Mandatory = $true)]
    [switch] $AuthorizeServiceChanges,

    [switch] $KeepWorkingDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $AuthorizeServiceChanges) {
    throw 'Pass -AuthorizeServiceChanges to acknowledge temporary Windows Service creation and deletion.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Windows Service verification requires an elevated Windows PowerShell 5.1 session.'
}
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Refusing to replace an existing service: $ServiceName"
}

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$workingRoot = Join-Path $temporaryParent ("monkeysphere-service-verify-" + [guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $workingRoot 'package'
$dataRoot = Join-Path $workingRoot 'data'
$passwordFile = Join-Path $workingRoot 'admin-password'
$serviceCreated = $false

function Wait-ServiceState([string] $Name, [System.ServiceProcess.ServiceControllerStatus] $Status) {
    $service = Get-Service -Name $Name -ErrorAction Stop
    $service.WaitForStatus($Status, [TimeSpan]::FromSeconds(30))
}

New-Item -ItemType Directory -Path $packageRoot, $dataRoot | Out-Null
Set-Content -LiteralPath $passwordFile -Value 'admin' -Encoding UTF8
try {
    Expand-Archive -LiteralPath $resolvedPackage -DestinationPath $packageRoot
    $executable = Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter 'Monkeysphere.Web.exe' | Select-Object -First 1
    if ($null -eq $executable) {
        throw 'The package does not contain Monkeysphere.Web.exe.'
    }

    $binaryPath = '"{0}" --urls http://127.0.0.1:{1} --MONKEYSPHERE_DATA_ROOT "{2}" --MONKEYSPHERE_ADMIN_USERNAME admin --MONKEYSPHERE_ADMIN_PASSWORD_FILE "{3}"' -f `
        $executable.FullName, $Port, $dataRoot, $passwordFile
    New-Service -Name $ServiceName `
        -BinaryPathName $binaryPath `
        -DisplayName 'Monkeysphere deployment verification' `
        -Description 'Temporary Monkeysphere Windows Service lifecycle verification.' `
        -StartupType Manual | Out-Null
    $serviceCreated = $true

    Start-Service -Name $ServiceName
    Wait-ServiceState -Name $ServiceName -Status Running
    & (Join-Path $PSScriptRoot 'TestWebDeployment.ps1') -BaseUri "http://127.0.0.1:$Port"
    Stop-Service -Name $ServiceName
    Wait-ServiceState -Name $ServiceName -Status Stopped

    if (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'monkeysphere.db') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $dataRoot 'remote-access.db') -PathType Leaf)) {
        throw 'The Windows Service did not create both managed SQLite databases.'
    }

    Start-Service -Name $ServiceName
    Wait-ServiceState -Name $ServiceName -Status Running
    & (Join-Path $PSScriptRoot 'TestWebDeployment.ps1') -BaseUri "http://127.0.0.1:$Port"
    Write-Host "Windows Service start, stop, restart, login, and persistent-data smoke passed for $ServiceName."
}
finally {
    if ($serviceCreated) {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -ne $service -and $service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            try { Wait-ServiceState -Name $ServiceName -Status Stopped } catch { Write-Warning $_ }
        }
        & sc.exe delete $ServiceName | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Windows could not delete temporary service $ServiceName."
        }
    }

    if ($KeepWorkingDirectory) {
        Write-Host "Verification files retained at $workingRoot"
    }
    elseif (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        $resolvedWorkingRoot = [IO.Path]::GetFullPath($workingRoot)
        if (-not $resolvedWorkingRoot.StartsWith($temporaryParent, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedWorkingRoot) -notlike 'monkeysphere-service-verify-*') {
            throw "Refusing to remove unexpected verification path: $resolvedWorkingRoot"
        }
        Remove-Item -LiteralPath $resolvedWorkingRoot -Recurse -Force
    }
    else {
        Write-Warning "Verification files were retained because service $ServiceName still exists: $workingRoot"
    }
}
