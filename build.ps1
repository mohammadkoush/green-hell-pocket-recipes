# Build PocketRecipes.dll and (optionally) deploy it into Green Hell's BepInEx plugins folder.
#
# Uses the stock .NET Framework csc.exe, so no Visual Studio or SDK is needed. Reference assemblies
# are taken straight from the game install, which means the build always matches the installed
# game version.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1            # build + deploy
#   powershell -ExecutionPolicy Bypass -File build.ps1 -NoDeploy  # build only
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Green Hell',
    [switch]$NoDeploy
)
$ErrorActionPreference = 'Stop'

$managed = Join-Path $GameDir 'GH_Data\Managed'
$core    = Join-Path $GameDir 'BepInEx\core'
$csc     = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$srcDir  = $PSScriptRoot
$outDir  = Join-Path $srcDir 'build'
$outDll  = Join-Path $outDir 'PocketRecipes.dll'

foreach ($p in @($managed, $core, $csc)) {
    if (-not (Test-Path $p)) { throw "Not found: $p" }
}
New-Item -ItemType Directory -Force $outDir | Out-Null

# Minimal reference set: the game assembly, the Unity modules actually used, and BepInEx.
$refs = @(
    (Join-Path $managed 'Assembly-CSharp.dll')
    (Join-Path $managed 'UnityEngine.dll')
    (Join-Path $managed 'UnityEngine.CoreModule.dll')
    (Join-Path $managed 'UnityEngine.PhysicsModule.dll')
    (Join-Path $managed 'UnityEngine.InputLegacyModule.dll')
    (Join-Path $managed 'UnityEngine.IMGUIModule.dll')
    # UnityEngine.UI: the wheel picker writes its count into the game's own right-click menu, whose
    # buttons carry UnityEngine.UI.Text labels.
    (Join-Path $managed 'UnityEngine.UI.dll')
    # TextRenderingModule: TextAnchor / FontStyle, used to centre the highlight markers.
    (Join-Path $managed 'UnityEngine.TextRenderingModule.dll')
    # AssetBundleModule: loads the outline shader bundle built by unity-outline/.
    (Join-Path $managed 'UnityEngine.AssetBundleModule.dll')
    # UnityEngine.AnimationModule: Highlight.AnimateWithin holds an outlined creature's Animator on
    # AlwaysAnimate, because a culled animator stops SKINNING and the outline then freezes mid-stride.
    (Join-Path $managed 'UnityEngine.AnimationModule.dll')
    (Join-Path $core    'BepInEx.dll')
    # Harmony: needed only to stop the game opening its own pause menu on the same Escape press that
    # closes our settings window. Event.Use() cannot do it - the game reads Escape through the legacy
    # Input polling in its own Update, which never sees the consumed IMGUI event.
    (Join-Path $core    '0Harmony.dll')
)
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "Missing reference: $r" } }

$sources = @(Get-ChildItem $srcDir -Filter *.cs | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "No .cs sources found in $srcDir" }

# Build one flat argument list. Splatting a single-element array in PowerShell would splat the
# string's characters instead, so assemble the list explicitly.
$argList = New-Object 'System.Collections.Generic.List[string]'
$argList.Add('/nologo')
$argList.Add('/target:library')
$argList.Add('/optimize+')
$argList.Add('/warn:3')
$argList.Add('/out:' + $outDll)
foreach ($r in $refs)    { $argList.Add('/reference:' + $r) }
foreach ($s in $sources) { $argList.Add($s) }

Write-Host "Compiling PocketRecipes..." -ForegroundColor Cyan
& $csc $argList.ToArray()
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED (csc exit $LASTEXITCODE)" -ForegroundColor Red; exit 1 }
Write-Host "Built $outDll" -ForegroundColor Green

if ($NoDeploy) { Write-Host "Skipping deploy (-NoDeploy)." -ForegroundColor Yellow; exit 0 }

# Deploy into its own subfolder so a mod manager never confuses it with another plugin.
$dest = Join-Path $GameDir 'BepInEx\plugins\PocketRecipes'
New-Item -ItemType Directory -Force $dest | Out-Null

# The game holds the deployed DLL as a mapped section while it runs, so a copy over it fails with an
# opaque IOException. Say what that actually means.
$running = @(Get-Process -Name 'GH' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host ""
    Write-Host "BUILD OK, DEPLOY SKIPPED: Green Hell is running (PID $($running[0].Id))." -ForegroundColor Yellow
    Write-Host "The game has the old DLL locked. Close the game, then re-run this script." -ForegroundColor Yellow
    Write-Host "The fresh build is waiting at: $outDll" -ForegroundColor Cyan
    exit 2
}
try {
    Copy-Item $outDll (Join-Path $dest 'PocketRecipes.dll') -Force -ErrorAction Stop
} catch {
    Write-Host ""
    Write-Host "BUILD OK, DEPLOY FAILED: could not overwrite the deployed DLL." -ForegroundColor Yellow
    Write-Host "This almost always means Green Hell is still running. Close it and re-run." -ForegroundColor Yellow
    Write-Host "The fresh build is waiting at: $outDll" -ForegroundColor Cyan
    exit 2
}
Write-Host "Deployed -> $dest" -ForegroundColor Green
Write-Host "Launch the game, then read BepInEx\LogOutput.log for lines tagged [Pocket Recipes]." -ForegroundColor Yellow
