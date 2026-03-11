param()

$ErrorActionPreference = "Stop"

function Get-GitConfigValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Key
    )

    $value = git config --global $Key 2>$null
    if ([string]::IsNullOrWhiteSpace($value)) {
        return "<unset>"
    }

    return $value.Trim()
}

$report = [ordered]@{
    "user.name"       = Get-GitConfigValue "user.name"
    "user.email"      = Get-GitConfigValue "user.email"
    "gpg.format"      = Get-GitConfigValue "gpg.format"
    "user.signingkey" = Get-GitConfigValue "user.signingkey"
    "commit.gpgsign"  = Get-GitConfigValue "commit.gpgsign"
    "tag.gpgSign"     = Get-GitConfigValue "tag.gpgSign"
}

Write-Host "Git signing configuration (global):"
foreach ($entry in $report.GetEnumerator()) {
    Write-Host ("- {0}: {1}" -f $entry.Key, $entry.Value)
}

$missing = @()
if ($report["user.email"] -eq "<unset>") { $missing += "user.email" }
if ($report["user.signingkey"] -eq "<unset>") { $missing += "user.signingkey" }
if ($report["commit.gpgsign"] -eq "<unset>") { $missing += "commit.gpgsign" }
if ($report["tag.gpgSign"] -eq "<unset>") { $missing += "tag.gpgSign" }

if ($missing.Count -gt 0) {
    Write-Warning ("Missing recommended keys: {0}" -f ($missing -join ", "))
    exit 1
}

Write-Host "OK: signing baseline is configured."
