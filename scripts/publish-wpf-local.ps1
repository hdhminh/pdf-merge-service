param(
    [string]$Version = "1.3.17-local",
    [string]$Channel = "stable"
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not (Test-Path "node_modules")) {
        throw "node_modules missing. Run 'npm ci --omit=dev' first."
    }

    & "$PSScriptRoot/clean-local-artifacts.ps1"
    & "$PSScriptRoot/prune-backend-payload.ps1"
    & "$PSScriptRoot/ensure-bundled-node.ps1"
    & "$PSScriptRoot/ensure-bundled-ngrok.ps1"

    $sigOut = "tools/signature-field-tool/SignatureFieldTool/publish/win-x64"
    if (Test-Path $sigOut) {
        Remove-Item -Path $sigOut -Recurse -Force
    }

    dotnet publish tools/signature-field-tool/SignatureFieldTool/SignatureFieldTool.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $sigOut
    if ($LASTEXITCODE -ne 0) {
        throw "Signature-field publish failed with exit code $LASTEXITCODE"
    }

    dotnet publish desktop-app-wpf/PdfStampNgrokDesktop.csproj `
        -c Release `
        -r win-x64 `
        -p:PublishSingleFile=true `
        -p:SelfContained=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:Version=$Version `
        -p:UpdateChannel=$Channel `
        -o artifacts/publish/win-x64
    if ($LASTEXITCODE -ne 0) {
        throw "Desktop app publish failed with exit code $LASTEXITCODE"
    }

    $bytes = (Get-ChildItem artifacts/publish/win-x64 -Recurse -File | Measure-Object Length -Sum).Sum
    $mb = [math]::Round($bytes / 1MB, 2)
    Write-Host "Local publish completed: artifacts/publish/win-x64 ($mb MB)"
}
finally {
    Pop-Location
}
