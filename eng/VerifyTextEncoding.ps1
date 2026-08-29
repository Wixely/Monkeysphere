[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
$textExtensions = @(
    '.cs', '.razor', '.md', '.json', '.xml', '.props', '.targets',
    '.yml', '.yaml', '.css', '.js', '.ps1', '.sh', '.html',
    '.csproj', '.slnx', '.service', '.svg', '.txt'
)
$extensionlessTextFiles = @('Dockerfile', '.dockerignore', '.gitignore')
$problems = New-Object System.Collections.Generic.List[string]

Push-Location -LiteralPath $repositoryRoot
try {
    $trackedFiles = & git ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate tracked files.' }

    foreach ($relativePath in $trackedFiles) {
        $extension = [IO.Path]::GetExtension($relativePath).ToLowerInvariant()
        $name = [IO.Path]::GetFileName($relativePath)
        if (($extension -notin $textExtensions) -and ($name -notin $extensionlessTextFiles)) { continue }
        if ($relativePath -like 'src/Monkeysphere.Web/wwwroot/vendor/*') { continue }

        $path = Join-Path $repositoryRoot $relativePath
        try {
            $content = [IO.File]::ReadAllText($path, $strictUtf8)
        }
        catch {
            $problems.Add("Invalid UTF-8: $relativePath")
            continue
        }

        if ($content.IndexOf([char]0xFEFF) -ge 0) {
            $problems.Add("Embedded byte-order mark: $relativePath")
        }
        if ($content.IndexOf([char]0xFFFD) -ge 0) {
            $problems.Add("Unicode replacement character: $relativePath")
        }
        if ([regex]::IsMatch($content, '[\u202A-\u202E\u2066-\u2069\u200B-\u200F]')) {
            $problems.Add("Invisible directional or formatting character: $relativePath")
        }
        if ([regex]::IsMatch($content, '[\u00C2\u00C3].|\u00E2[\u0080-\u00BF]')) {
            $problems.Add("Possible UTF-8 mojibake: $relativePath")
        }
    }
}
finally {
    Pop-Location
}

if ($problems.Count -gt 0) {
    throw "Text encoding verification failed:`n$($problems -join "`n")"
}

Write-Host 'Text encoding verification passed.'
