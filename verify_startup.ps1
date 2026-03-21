# verify_startup.ps1
# Builds Nagi, launches it, waits 60s, kills it, then parses the startup log.
# Outputs: STARTUP_TOTAL_MS=<number>
# Exit code: 0 on success, 1 on failure.

$ErrorActionPreference = 'Stop'

$startupLogPath = "$env:USERPROFILE\Downloads\nagi_startup.log"

# ── 1. Build ──────────────────────────────────────────────────────────────────
Write-Host "[verify] Building Nagi.WinUI..." -ForegroundColor Cyan

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { Write-Error "vswhere.exe not found."; exit 1 }

$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe
if (-not $msbuild) { Write-Error "MSBuild.exe not found."; exit 1 }

& $msbuild src\Nagi.WinUI\Nagi.WinUI.csproj /p:Configuration=Debug /p:Platform=x64 /v:m /nologo
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed."; exit 1 }

Write-Host "[verify] Build succeeded." -ForegroundColor Green

# ── 2. Sync build output to the AppX folder (installed package location) ──────
# The package is registered to AppX\ but MSBuild outputs to the parent win-x64\ folder.
# Copying ensures the running app uses the freshly compiled binaries.
Write-Host "[verify] Syncing build output to AppX folder..." -ForegroundColor Cyan
$buildOut = "src\Nagi.WinUI\bin\x64\Debug\net10.0-windows10.0.26100\win-x64"
$appxDir  = "$buildOut\AppX"
Get-ChildItem "$buildOut\*.dll", "$buildOut\*.exe", "$buildOut\*.json" -ErrorAction SilentlyContinue |
    ForEach-Object { Copy-Item $_.FullName $appxDir -Force }
Write-Host "[verify] Sync done." -ForegroundColor Green

# ── 3. Launch ─────────────────────────────────────────────────────────────────
Write-Host "[verify] Launching Nagi..." -ForegroundColor Cyan
explorer.exe shell:AppsFolder\48743Nagi.Nagi_ejgr3wwtkesbm!App

# ── 4. Wait 15 seconds for startup to complete ────────────────────────────────
Write-Host "[verify] Waiting 15 seconds for app to start..." -ForegroundColor Cyan
Start-Sleep -Seconds 15

# ── 4. Kill the process ───────────────────────────────────────────────────────
Write-Host "[verify] Killing Nagi process..." -ForegroundColor Cyan
$processes = Get-Process -Name "Nagi" -ErrorAction SilentlyContinue
if ($processes) {
    $processes | Stop-Process -Force
    Write-Host "[verify] Process killed." -ForegroundColor Green
} else {
    Write-Warning "[verify] No Nagi process found to kill (may have already exited)."
}

# Give the process a moment to flush its log
Start-Sleep -Seconds 2

# ── 5. Parse startup log ──────────────────────────────────────────────────────
if (-not (Test-Path $startupLogPath)) {
    Write-Error "[verify] Startup log not found at: $startupLogPath"
    exit 1
}

Write-Host "[verify] Startup log contents:" -ForegroundColor Cyan
Get-Content $startupLogPath | ForEach-Object { Write-Host "  $_" }

$match = Get-Content $startupLogPath | Select-String 'STARTUP_TOTAL_MS=(\d+)'
if (-not $match) {
    Write-Error "[verify] STARTUP_TOTAL_MS marker not found in log. App may not have finished loading within 60 seconds."
    exit 1
}

$totalMs = $match.Matches[0].Groups[1].Value
Write-Host ""
Write-Host "STARTUP_TOTAL_MS=$totalMs" -ForegroundColor Green
exit 0
