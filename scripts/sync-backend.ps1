param(
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$sourceFiles = @(
    @{ From = "index.js"; To = "desktop-app\\backend\\index.js" },
    @{ From = "pdfStamp.js"; To = "desktop-app\\backend\\pdfStamp.js" },
    @{ From = "config\\stamp-profiles.json"; To = "desktop-app\\backend\\config\\stamp-profiles.json" }
)

foreach ($entry in $sourceFiles) {
    $fromPath = Join-Path $RepoRoot $entry.From
    $toPath = Join-Path $RepoRoot $entry.To

    if (!(Test-Path -LiteralPath $fromPath)) {
        throw "Missing source file: $fromPath"
    }

    $destDir = Split-Path -Parent $toPath
    if (!(Test-Path -LiteralPath $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }

    Copy-Item -LiteralPath $fromPath -Destination $toPath -Force
    Write-Host "Synced $($entry.From) -> $($entry.To)"
}

Write-Host "Backend mirror sync complete."
