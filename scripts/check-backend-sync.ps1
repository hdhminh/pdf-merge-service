param(
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$checks = @(
    @{ Source = "index.js"; Mirror = "desktop-app\\backend\\index.js" },
    @{ Source = "pdfStamp.js"; Mirror = "desktop-app\\backend\\pdfStamp.js" },
    @{ Source = "config\\stamp-profiles.json"; Mirror = "desktop-app\\backend\\config\\stamp-profiles.json" }
)

$errors = New-Object System.Collections.Generic.List[string]

foreach ($entry in $checks) {
    $sourcePath = Join-Path $RepoRoot $entry.Source
    $mirrorPath = Join-Path $RepoRoot $entry.Mirror

    if (!(Test-Path -LiteralPath $sourcePath)) {
        $errors.Add("Missing source file: $sourcePath")
        continue
    }
    if (!(Test-Path -LiteralPath $mirrorPath)) {
        $errors.Add("Missing mirror file: $mirrorPath")
        continue
    }

    $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    $mirrorHash = (Get-FileHash -LiteralPath $mirrorPath -Algorithm SHA256).Hash
    if ($sourceHash -ne $mirrorHash) {
        $errors.Add("Mismatch: $($entry.Source) <> $($entry.Mirror)")
    } else {
        Write-Host "OK: $($entry.Source) == $($entry.Mirror)"
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    throw "Backend mirror check failed. Run scripts/sync-backend.ps1."
}

Write-Host "Backend mirror check passed."
