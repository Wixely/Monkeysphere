[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https?://')]
    [string] $BaseUri,

    [string] $Username = 'admin',
    [string] $Password = 'admin',

    [ValidateRange(5, 300)]
    [int] $TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$baseAddress = $BaseUri.TrimEnd('/')
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$ready = $false
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    try {
        $live = Invoke-WebRequest -UseBasicParsing -Uri "$baseAddress/health/live" -TimeoutSec 5
        $readiness = Invoke-WebRequest -UseBasicParsing -Uri "$baseAddress/health/ready" -TimeoutSec 5
        if ($live.StatusCode -eq 200 -and $readiness.StatusCode -eq 200) {
            $ready = $true
            break
        }
    }
    catch {
        Start-Sleep -Milliseconds 500
    }
}

if (-not $ready) {
    throw "Monkeysphere did not become ready at $baseAddress within $TimeoutSeconds seconds."
}

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$login = Invoke-WebRequest -UseBasicParsing -Uri "$baseAddress/login" -WebSession $session -TimeoutSec 10
$tokenMatch = [regex]::Match(
    $login.Content,
    'name="__RequestVerificationToken"[^>]*value="([^"]+)"',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $tokenMatch.Success) {
    throw 'The login page did not contain an antiforgery token.'
}

$form = @{
    __RequestVerificationToken = [Net.WebUtility]::HtmlDecode($tokenMatch.Groups[1].Value)
    username = $Username
    password = $Password
    returnUrl = '/setup'
}
Invoke-WebRequest -UseBasicParsing -Uri "$baseAddress/auth/login" -Method Post -Body $form -WebSession $session -MaximumRedirection 5 -TimeoutSec 10 | Out-Null
$privatePage = Invoke-WebRequest -UseBasicParsing -Uri "$baseAddress/setup" -WebSession $session -MaximumRedirection 5 -TimeoutSec 10
if ($privatePage.BaseResponse.ResponseUri.AbsolutePath -eq '/login' -or $privatePage.Content -match '<h1>Sign in</h1>') {
    throw 'The configured administrator credentials did not establish an authenticated session.'
}
if ($privatePage.Content -notmatch 'What would you like to remember\?|Setup is complete') {
    throw 'The authenticated setup surface did not render its expected state.'
}

Write-Host "Deployment smoke passed at $baseAddress."
