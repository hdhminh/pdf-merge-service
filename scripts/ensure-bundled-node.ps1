param(
    [string]$TargetDir = "bin/node-win-x64"
)

$ErrorActionPreference = "Stop"

$targetExe = Join-Path $TargetDir "node.exe"
if (Test-Path $targetExe) {
    Write-Host "Using existing Node runtime: $targetExe"
    exit 0
}

$nodeVersion = (node -p "process.version").TrimStart('v')
if (-not $nodeVersion) {
    throw "Cannot resolve Node version."
}

New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
$zipPath = Join-Path $env:TEMP "node-v$nodeVersion-win-x64.zip"
$shaPath = Join-Path $env:TEMP "SHASUMS256.txt"
$extractDir = Join-Path $env:TEMP "node-portable-win-x64"
$url = "https://nodejs.org/dist/v$nodeVersion/node-v$nodeVersion-win-x64.zip"

Invoke-WebRequest -Uri $url -OutFile $zipPath
Invoke-WebRequest -Uri "https://nodejs.org/dist/v$nodeVersion/SHASUMS256.txt" -OutFile $shaPath

$expectedLine = Get-Content $shaPath |
    Where-Object { $_ -match " node-v$nodeVersion-win-x64.zip$" } |
    Select-Object -First 1
if (-not $expectedLine) {
    throw "Cannot resolve Node checksum from SHASUMS256.txt."
}

$expectedHash = ($expectedLine -split "\s+")[0].Trim().ToLowerInvariant()
$actualHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    throw "Node runtime checksum mismatch. expected=$expectedHash actual=$actualHash"
}

if (Test-Path $extractDir) {
    Remove-Item -Recurse -Force $extractDir
}
Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

$expandedRoot = Get-ChildItem -Path $extractDir -Directory | Select-Object -First 1
if ($null -eq $expandedRoot) {
    throw "Cannot extract bundled Node runtime."
}

Copy-Item -Path (Join-Path $expandedRoot.FullName "*") -Destination $TargetDir -Recurse -Force
if (-not (Test-Path $targetExe)) {
    throw "node.exe was not found after extraction."
}

Write-Host "Bundled Node runtime ready at: $targetExe"
