param(
    [string]$NodeModulesRoot = "node_modules"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $NodeModulesRoot)) {
    throw "node_modules not found at '$NodeModulesRoot'."
}

function Get-DirSizeMb([string]$path) {
    $sum = (Get-ChildItem -Path $path -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return 0.0 }
    return [math]::Round($sum / 1MB, 2)
}

$beforeMb = Get-DirSizeMb $NodeModulesRoot
$pruneDirNames = @("test", "tests", "__tests__", "doc", "docs", "example", "examples", "benchmark", "benchmarks", ".github", ".vscode", ".idea", "coverage")

Get-ChildItem -Path $NodeModulesRoot -Directory -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $pruneDirNames -contains $_.Name.ToLowerInvariant() } |
    ForEach-Object { Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

Get-ChildItem -Path $NodeModulesRoot -File -Recurse -Filter "*.map" -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $NodeModulesRoot -File -Recurse -Filter "*.tsbuildinfo" -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

# Keep only runtime-relevant pdfjs-dist assets used by backend:
# - require("pdfjs-dist/legacy/build/pdf.js")
# - standard_fonts (for font data URL)
$pdfjsRoot = Join-Path $NodeModulesRoot "pdfjs-dist"
$pdfjsPrunePaths = @(
    (Join-Path $pdfjsRoot "build"),
    (Join-Path $pdfjsRoot "types"),
    (Join-Path $pdfjsRoot "web"),
    (Join-Path $pdfjsRoot "image_decoders"),
    (Join-Path $pdfjsRoot "legacy/web"),
    (Join-Path $pdfjsRoot "legacy/image_decoders")
)
foreach ($path in $pdfjsPrunePaths) {
    if (Test-Path $path) {
        Remove-Item -Path $path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$afterMb = Get-DirSizeMb $NodeModulesRoot
Write-Host "node_modules size before prune: $beforeMb MB"
Write-Host "node_modules size after prune:  $afterMb MB"
