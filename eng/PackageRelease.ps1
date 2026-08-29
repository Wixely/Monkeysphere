[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Za-z][0-9A-Za-z.-]{0,99}$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $repositoryRoot ".artifacts\releases\$Version"
$stagingRoot = Join-Path $releaseRoot 'staging'
$checksumPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
$runtimes = @('win-x64', 'linux-x64')

if (Test-Path -LiteralPath $releaseRoot) {
    throw "Release output already exists: $releaseRoot"
}

Push-Location -LiteralPath $repositoryRoot
try {
    $checksums = @()
    foreach ($runtime in $runtimes) {
        $runtimeRoot = Join-Path $stagingRoot $runtime
        $packageRoot = Join-Path $runtimeRoot 'monkeysphere'
        $archivePath = Join-Path $releaseRoot "monkeysphere-$Version-$runtime.zip"
        New-Item -ItemType Directory -Path $packageRoot | Out-Null
        dotnet publish src\Monkeysphere.Web\Monkeysphere.Web.csproj `
            --configuration Release `
            --runtime $runtime `
            --self-contained false `
            --no-restore `
            --output $packageRoot | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Release publish failed for $runtime." }

        Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter '*.pdb' | Remove-Item -Force
        Copy-Item -LiteralPath README.md, LICENSE, THIRD-PARTY-NOTICES.md -Destination $packageRoot
        Copy-Item -LiteralPath deploy -Destination $packageRoot -Recurse
        Copy-Item -LiteralPath docs -Destination $packageRoot -Recurse
        Set-Content -LiteralPath (Join-Path $packageRoot 'RELEASE-VERSION') -Value $Version -Encoding ASCII

        Compress-Archive -LiteralPath $packageRoot -DestinationPath $archivePath -CompressionLevel Optimal
        $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Host "Release package: $archivePath"
        $checksums += "$hash  $(Split-Path -Leaf $archivePath)"
    }
    Set-Content -LiteralPath $checksumPath -Value $checksums -Encoding ASCII
    Write-Host "Checksums: $checksumPath"
}
finally {
    Pop-Location
}
