[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryRoot
try {
    dotnet run --project src\Monkeysphere.Web\Monkeysphere.Web.csproj --launch-profile http
    if ($LASTEXITCODE -ne 0) { throw 'Monkeysphere exited with an error.' }
}
finally {
    Pop-Location
}
