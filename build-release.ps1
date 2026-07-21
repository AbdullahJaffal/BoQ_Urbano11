<#
  build-release.ps1 - UrbanoMetraj production packaging pipeline (BricsCAD edition).

  Steps:
    1. dotnet build -c Release            (bin\Release\net48\UrbanoMetraj.dll, BricsCAD build)
    2. ConfuserEx2 obfuscation            (dist\UrbanoMetraj.crproj -> bin\Release\net48\confused)
    3. Assemble dist\UrbanoMetraj.bundle    (bundle template + OBFUSCATED dll + EPPlus + templates)
    4. Compile Inno Setup installer       (installer\UrbanoMetraj.iss, if ISCC found)
                                          -> installer\Output\UrbanoMetraj-BricsCAD-Setup-1.1.0.exe

  Usage:
    powershell -ExecutionPolicy Bypass -File build-release.ps1 -ConfuserCli "C:\Tools\ConfuserEx\Confuser.CLI.exe" -Iscc "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

    Skip obfuscation (debug packaging; ships RAW dll, not for production):
    powershell -ExecutionPolicy Bypass -File build-release.ps1 -SkipObfuscate
#>

[CmdletBinding()]
param(
    [string] $ConfuserCli = "",
    [string] $Iscc        = "",
    [switch] $SkipObfuscate,
    [string] $MSBuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe",
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root      = $PSScriptRoot
# UrbanoMetraj is an OLD-STYLE csproj, so MSBuild writes straight to
# bin\Release (no net48 sub-folder like the SDK-style UrbanoLock project).
$binDir    = Join-Path $root "bin\$Configuration"
$distDir   = Join-Path $root "dist"
# ConfuserEx writes to <baseDir>\<outputDir> = bin\Release\net48\confused
$confused  = Join-Path $binDir "confused"
$srcBundle = Join-Path $root "bundle\UrbanoMetraj.bundle"
$outBundle = Join-Path $distDir "UrbanoMetraj.bundle"
$crproj    = Join-Path $distDir "UrbanoMetraj.crproj"
$iss       = Join-Path $root "installer\UrbanoMetraj.iss"

function Step($m) { Write-Host ("`n=== " + $m + " ===") -ForegroundColor Cyan }

# Run a native .exe whose progress goes to stderr (ConfuserEx, ISCC). Under
# $ErrorActionPreference='Stop', native stderr is otherwise promoted to a
# terminating error even on exit code 0; we relax it locally and judge success
# purely by the real exit code.
function Invoke-Tool([string]$exe, [string[]]$toolArgs, [string]$failMsg) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $exe @toolArgs 2>&1 | ForEach-Object { Write-Host $_ }
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prev
    }
    if ($code -ne 0) { throw $failMsg }
}

# 1. Build
Step "1/4  dotnet build -c $Configuration  (BricsCAD)"
Invoke-Tool $MSBuild @((Join-Path $root "UrbanoMetraj.csproj"), "/t:Restore,Build",
                       "/p:Configuration=$Configuration", "/p:Platform=x64",
                       "/v:minimal", "/nologo") "UrbanoMetraj build failed."
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
$rawDll = Join-Path $binDir "UrbanoMetraj.dll"
if (-not (Test-Path $rawDll)) { throw ("Missing build output: " + $rawDll) }

# The .csproj post-build copies the RAW dev DLL into the PROJECT bundle only
# (bundle\UrbanoMetraj.bundle). For a RELEASE package we must not let a stale RAW
# bundle linger in BricsCAD's ApplicationPlugins and shadow the obfuscated build.
$devBundle = Join-Path $env:APPDATA "Bricsys\BricsCAD\V23x64\en_US\Support\ApplicationPlugins\UrbanoMetraj.bundle"
if (Test-Path $devBundle) {
    Remove-Item $devBundle -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Removed any dev bundle from BricsCAD ApplicationPlugins (prevents shadowing the installer build)." -ForegroundColor DarkGray
}

# 2. Obfuscate
$finalDll = $rawDll
if ($SkipObfuscate) {
    Write-Warning "Obfuscation SKIPPED - shipping the raw DLL (NOT for production)."
} else {
    Step "2/4  ConfuserEx2 obfuscation"
    if ([string]::IsNullOrWhiteSpace($ConfuserCli)) {
        $cmd = Get-Command "Confuser.CLI.exe" -ErrorAction SilentlyContinue
        if ($cmd) { $ConfuserCli = $cmd.Source }
    }
    if ([string]::IsNullOrWhiteSpace($ConfuserCli) -or -not (Test-Path $ConfuserCli)) {
        throw "Confuser.CLI.exe not found. Download ConfuserEx2 and pass -ConfuserCli PATH, or run with -SkipObfuscate."
    }
    if (Test-Path $confused) { Remove-Item $confused -Recurse -Force }
    Invoke-Tool $ConfuserCli @($crproj, "-n") "Obfuscation failed."
    $finalDll = Join-Path $confused "UrbanoMetraj.dll"
    if (-not (Test-Path $finalDll)) { throw ("Obfuscated DLL not produced: " + $finalDll) }
    Write-Host ("Obfuscated DLL: " + $finalDll) -ForegroundColor Green
}

# 3. Assemble the distributable bundle
Step "3/4  Assemble dist\UrbanoMetraj.bundle"
if (Test-Path $outBundle) { Remove-Item $outBundle -Recurse -Force }
Copy-Item $srcBundle $outBundle -Recurse -Force
Copy-Item $finalDll (Join-Path $outBundle "Contents\Windows\UrbanoMetraj.dll") -Force

# Shared licence core. It is a plain dependency: NOT a ComponentEntry and NOT
# listed in appload.dfs - each plugin resolves it from this same folder via its
# AssemblyResolve handler, so the CLR loads it ONCE and both products share a
# single licence session lease. Take it from the same place as the plugin dll
# (confused\ when obfuscated) so a release never mixes raw + protected output.
$licSrc = Join-Path (Split-Path $finalDll -Parent) "UrbanoLicensing.dll"
if (-not (Test-Path $licSrc)) { $licSrc = Join-Path $binDir "UrbanoLicensing.dll" }
if (-not (Test-Path $licSrc)) { throw ("Missing licence core: " + $licSrc) }
Copy-Item $licSrc (Join-Path $outBundle "Contents\Windows\UrbanoLicensing.dll") -Force
Write-Host ("Licence core: " + $licSrc) -ForegroundColor Green
# Third-party deps, shipped untouched (never obfuscated):
#   Clipper2Lib - polygon boolean engine used by the BoQ geometry pipeline
#   EPPlus      - Excel writer for the Metraj export; WITHOUT it every export
#                 throws FileNotFoundException at runtime.
foreach ($dep in @("Clipper2Lib.dll", "EPPlus.dll")) {
    $src = Join-Path $binDir $dep
    if (-not (Test-Path $src)) { throw ("Missing dependency: " + $src) }
    Copy-Item $src (Join-Path $outBundle "Contents\Windows\$dep") -Force
    Write-Host ("Dependency: " + $dep) -ForegroundColor Green
}
Write-Host ("Bundle ready: " + $outBundle) -ForegroundColor Green

# 4. Installer
Step "4/4  Inno Setup installer"
if ([string]::IsNullOrWhiteSpace($Iscc)) {
    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    foreach ($c in $candidates) { if (Test-Path $c) { $Iscc = $c; break } }
}
if ([string]::IsNullOrWhiteSpace($Iscc) -or -not (Test-Path $Iscc)) {
    Write-Warning "ISCC.exe (Inno Setup) not found - skipping installer compile."
    Write-Warning ("Install Inno Setup 6 and re-run, or compile manually: ISCC.exe " + $iss)
} else {
    Invoke-Tool $Iscc @($iss) "Installer compile failed."
    Write-Host "Installer written to installer\Output\" -ForegroundColor Green
}

Step "DONE"
