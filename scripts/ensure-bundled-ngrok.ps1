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

$fallbackHash = "ff53d0913ae2dd4a49b9047a93c0fb838579a435af71cec35d4763170f960aab"
$providedHash = if ($null -eq $ExpectedZipSha256) { "" } else { $ExpectedZipSha256.Trim().ToLowerInvariant() }

Invoke-WebRequest -Uri "https://bin.equinox.io/c/bNyj1mQVY4c/ngrok-v3-stable-windows-amd64.zip" -OutFile $zipPath
$actualHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

$acceptedHashes = @($fallbackHash)
if ($providedHash) {
    $acceptedHashes = @($providedHash, $fallbackHash)
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
