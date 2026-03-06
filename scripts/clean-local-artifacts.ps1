$ErrorActionPreference = "Stop"

$targets = @(
    "artifacts",
    "tools/__pycache__",
    "tmp_mainvm_numbered.txt",
    "desktop-app-wpf/Assets/*.bak.ico",
    "desktop-app-wpf/Assets/*.bak.png",
    "desktop-app-wpf/Assets/*.bak.*",
    "desktop-app-wpf/Assets/guide-full-capture.png"
)

foreach ($target in $targets) {
    Get-Item -Path $target -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.PSIsContainer) {
            Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
        else {
            Remove-Item -Path $_.FullName -Force -ErrorAction SilentlyContinue
        }
        Write-Host "Removed: $($_.FullName)"
    }
}

Write-Host "Local artifact cleanup done."
