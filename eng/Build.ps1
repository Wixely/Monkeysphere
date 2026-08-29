[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryRoot
try {
    dotnet restore Monkeysphere.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

    dotnet build Monkeysphere.slnx --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    dotnet test Monkeysphere.slnx --configuration Release --no-build --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally {
    Pop-Location
}
