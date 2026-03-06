param(
    [string]$TargetDir = "bin/win32-x64",
    [string]$ExpectedZipSha256 = ""
)

$ErrorActionPreference = "Stop"

$targetExe = Join-Path $TargetDir "ngrok.exe"
if (Test-Path $targetExe) {
    Write-Host "Using existing ngrok binary: $targetExe"
    exit 0
}

New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
$zipPath = Join-Path $env:TEMP "ngrok-v3-windows-amd64.zip"

$expectedHash = if ($null -eq $ExpectedZipSha256) { "" } else { $ExpectedZipSha256.Trim().ToLowerInvariant() }
if (-not $expectedHash) {
    $expectedHash = "070b3202a473aff46f448e662ec35fdb63e3d87822f20180f3502e89d5d5a23f"
}

Invoke-WebRequest -Uri "https://bin.equinox.io/c/bNyj1mQVY4c/ngrok-v3-stable-windows-amd64.zip" -OutFile $zipPath
$actualHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    throw "ngrok zip checksum mismatch. expected=$expectedHash actual=$actualHash"
}

Expand-Archive -Path $zipPath -DestinationPath $TargetDir -Force
if (-not (Test-Path $targetExe)) {
    throw "ngrok.exe was not found after extraction."
}

Write-Host "Bundled ngrok ready at: $targetExe"
