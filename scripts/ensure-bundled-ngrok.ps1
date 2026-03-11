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

$fallbackHashes = @(
    # Current vendor hash (verified 2026-03-11)
    "c5909c7743497f3e390c965c8d9875832eb83ba5ebb8e25ffe07c7c8c4f36f14",
    # Previous trusted hash kept for vendor rollbacks
    "ff53d0913ae2dd4a49b9047a93c0fb838579a435af71cec35d4763170f960aab"
)
$providedHash = if ($null -eq $ExpectedZipSha256) { "" } else { $ExpectedZipSha256.Trim().ToLowerInvariant() }

Invoke-WebRequest -Uri "https://bin.equinox.io/c/bNyj1mQVY4c/ngrok-v3-stable-windows-amd64.zip" -OutFile $zipPath
$actualHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

$acceptedHashes = @($fallbackHashes)
if ($providedHash) {
    $acceptedHashes = @($providedHash) + $fallbackHashes
}

if ($acceptedHashes -notcontains $actualHash) {
    $expectedList = ($acceptedHashes -join ",")
    throw "ngrok zip checksum mismatch. expectedAny=[$expectedList] actual=$actualHash"
}

if ($providedHash -and $providedHash -ne $actualHash) {
    Write-Warning "NGROK_WIN_AMD64_SHA256 does not match vendor zip (provided=$providedHash actual=$actualHash). Falling back to built-in trusted hash."
}

Expand-Archive -Path $zipPath -DestinationPath $TargetDir -Force
if (-not (Test-Path $targetExe)) {
    throw "ngrok.exe was not found after extraction."
}

Write-Host "Bundled ngrok ready at: $targetExe"
