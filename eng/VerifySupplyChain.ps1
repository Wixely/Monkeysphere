[CmdletBinding()]
param(
    [switch] $AuditVulnerabilities
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$expectedHashes = [ordered]@{
    'eng\packages\DnaX.Data.Migrations.10.0.0-alpha.3.nupkg' = '550D81AFB35A3BC1F5911EE06025E89BD93C352DAF33645CFF04547C17B48573'
    'eng\packages\DnaX.Data.Migrations.Sqlite.10.0.0-alpha.3.nupkg' = 'C68C42DFB0F06DFBC5783717F38D5D691ECC1EFAF38BAA138C87DC97EE9BB048'
    'eng\packages\DnaX.Data.Migrations.Sqlite.Testing.10.0.0-alpha.3.nupkg' = 'C1A7525B749CBC0FE6EEC11C96F35476F76121A0643FB3B7909D704E54006557'
    'eng\packages\DnaX.Hosting.10.0.0-alpha.3.nupkg' = 'CE127CBF95184EFB0151414F15D4159C5BB4A9A5054D0CCF718C5B01D7AFBE40'
    'eng\packages\DnaX.RemoteAccess.10.0.0-alpha.3.nupkg' = '43D960E17800AE267D04915C8B032E60214F5E904DC87E6CA5215A49BA9CEA2F'
    'eng\packages\DnaX.RemoteAccess.Mcp.10.0.0-alpha.3.nupkg' = '79486D17D036A4B8BF7E5B7F53E78F39899175DEB78F00DED95848B904744732'
    'eng\packages\DnaX.RemoteAccess.Sqlite.10.0.0-alpha.3.nupkg' = 'C80245B991659C83B82DF22D6A7A211062A4D3DE8C952D6DBCE75BA5CCBBE553'
    'src\Monkeysphere.Web\wwwroot\vendor\openlayers\10.10.0\ol.js' = 'B89AF8EC3B76F564D515FD07FED3EC414AECF8F33F685B77B607451CB0C2029F'
    'src\Monkeysphere.Web\wwwroot\vendor\openlayers\10.10.0\ol.css' = 'ABC8AFD72CC10BD29CC143F443BAE4A6804BD3CB3FB262E6B6A6BC6C924EA34F'
    'src\Monkeysphere.Web\wwwroot\vendor\openlayers\10.10.0\LICENSE.md' = '6C4347B83A8C9FEEF18D57B18E3B6C44CF901B3C344A4A1FBD837E421555AB8E'
    'src\Monkeysphere.Web\wwwroot\vendor\cytoscape\3.34.0\cytoscape.min.js' = '9C2A3BF2592E0B14A1F7BEC07C03A54F16DEDF32AF9CD0AF155C716AA6C87BC3'
    'src\Monkeysphere.Web\wwwroot\vendor\cytoscape\3.34.0\LICENSE' = 'EB319C6E6F233607F71E8E2F450391751883CFC0EEB3CA7EF574C13D1D9C2203'
}

Push-Location -LiteralPath $repositoryRoot
try {
    foreach ($relativePath in $expectedHashes.Keys) {
        $fullPath = Join-Path $repositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Required vendored dependency is missing: $relativePath"
        }

        $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHashes[$relativePath]) {
            throw "Vendored dependency hash mismatch: $relativePath"
        }
    }

    $notice = Get-Content -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Raw
    foreach ($expectedHash in $expectedHashes.Values) {
        if (-not $notice.Contains($expectedHash)) {
            throw 'THIRD-PARTY-NOTICES.md does not contain every enforced dependency hash.'
        }
    }

    Get-ChildItem -LiteralPath $repositoryRoot -Recurse -Filter '*.csproj' | ForEach-Object {
        $project = Get-Content -LiteralPath $_.FullName -Raw
        if ($project -match '<PackageReference\s+[^>]*Version\s*=') {
            throw "Package versions must remain centrally pinned: $($_.FullName)"
        }
    }

    $runtimeSources = @(
        (Join-Path $repositoryRoot 'src\Monkeysphere.Web\Components\App.razor'),
        (Join-Path $repositoryRoot 'src\Monkeysphere.Web\wwwroot\app.css')
    ) + @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src\Monkeysphere.Web\wwwroot') -File -Filter '*.js' | Select-Object -ExpandProperty FullName)
    foreach ($runtimeSource in $runtimeSources) {
        if ((Get-Content -LiteralPath $runtimeSource -Raw) -match 'https?://') {
            throw "A first-party browser entry point contains a public HTTP dependency: $runtimeSource"
        }
    }

    $dockerfile = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Dockerfile') -Raw
    $requiredContainerPins = @(
        'mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c',
        'mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94',
        'RuntimeFrameworkVersion=10.0.8'
    )
    foreach ($requiredContainerPin in $requiredContainerPins) {
        if (-not $dockerfile.Contains($requiredContainerPin)) {
            throw "Dockerfile is missing an enforced container/runtime pin: $requiredContainerPin"
        }
    }
    $nestedDockerVerifier = Get-Content -LiteralPath (Join-Path $repositoryRoot 'eng\VerifyNestedDocker.sh') -Raw
    $nestedDockerPin = 'docker.io/library/docker:28.5.2-dind@sha256:2a232a42256f70d78e3cc5d2b5d6b3276710a0de0596c145f627ecfae90282ac'
    if (-not $nestedDockerVerifier.Contains($nestedDockerPin)) {
        throw "Nested Docker verifier is missing its enforced engine pin: $nestedDockerPin"
    }

    if ($AuditVulnerabilities) {
        $auditOutput = & dotnet package list --project Monkeysphere.slnx --vulnerable --include-transitive --no-restore --format json --output-version 1
        if ($LASTEXITCODE -ne 0) {
            throw 'NuGet vulnerability audit failed to run.'
        }

        $auditJson = $auditOutput -join [Environment]::NewLine
        if ($auditJson -match '"vulnerabilities"\s*:') {
            throw 'The NuGet audit reported one or more vulnerable packages.'
        }
    }

    Write-Host "Supply-chain verification passed for $($expectedHashes.Count) vendored files."
}
finally {
    Pop-Location
}
