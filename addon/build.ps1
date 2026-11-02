# Build DSPCalculatorFlat.dll with the .NET Framework C# compiler (Windows only, C# 5).
# Engine source, first match wins:
#   1. ..\bin\DspFlat.Engine.dll          referenced and shipped next to the plugin
#   2. ..\engine\*.cs with class FlatEngine compiled into the plugin
#   3. EngineStub.cs (/define:ENGINE_STUB) demo layouts only; also forced by -Stub
# Optionally install into an r2modman profile with -Install (the coordinator does that at integration).
param(
    [string]$GameDir = "C:\Program Files\Steam\steamapps\common\Dyson Sphere Program",
    [string]$Profile = "$env:APPDATA\r2modmanPlus-local\DysonSphereProgram\profiles\Default",
    [switch]$Stub,
    [switch]$Install
)
$ErrorActionPreference = 'Stop'

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$managed = Join-Path $GameDir "DSPGAME_Data\Managed"
$core = Join-Path $Profile "BepInEx\core"
$plugins = Join-Path $Profile "BepInEx\plugins"
$calcDll = Join-Path $plugins "jinxOAO-DSPCalculator\DSPCalculator.dll"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $PSScriptRoot "bin"
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($p in @("$managed\Assembly-CSharp.dll", "$core\BepInEx.dll", "$core\0Harmony.dll", $calcDll)) {
    if (-not (Test-Path $p)) { throw "Missing reference: $p" }
}

$refs = @(
    "$managed\mscorlib.dll", "$managed\netstandard.dll", "$managed\System.dll", "$managed\System.Core.dll",
    "$managed\Assembly-CSharp.dll", "$core\BepInEx.dll", "$core\0Harmony.dll", $calcDll
) + (Get-ChildItem $managed -Filter "UnityEngine*.dll" | ForEach-Object { $_.FullName })

$sources = @(Get-ChildItem (Join-Path $PSScriptRoot "src") -Filter *.cs | ForEach-Object { $_.FullName })
$defines = @()

$engineDll = Join-Path $root "bin\DspFlat.Engine.dll"
$engineDir = Join-Path $root "engine"
$engineCs = @()
if (Test-Path $engineDir) { $engineCs = @(Get-ChildItem $engineDir -Filter *.cs | ForEach-Object { $_.FullName }) }
$engineHasApi = $engineCs.Count -gt 0 -and (Select-String -Path $engineCs -Pattern 'class\s+FlatEngine\b' -Quiet)

$mode = ""
if ($Stub) { $mode = "stub" }
elseif (Test-Path $engineDll) { $mode = "dll" }
elseif ($engineHasApi) { $mode = "source" }
else { $mode = "stub" }

switch ($mode) {
    "dll"    { $refs += $engineDll; Write-Host "Engine: $engineDll" }
    "source" { $sources += $engineCs; Write-Host "Engine: $($engineCs.Count) source files from $engineDir" }
    "stub"   { $sources += (Join-Path $PSScriptRoot "EngineStub.cs"); $defines += "ENGINE_STUB"
               Write-Warning ("Engine: EngineStub.cs (" + $(if ($Stub) { "-Stub" } else { "no FlatEngine found" }) + ") - the plugin only produces demo layouts") }
}

$cscArgs = @('/nologo', '/noconfig', '/nostdlib+', '/target:library', '/optimize+', '/langversion:5', '/warn:4',
             "/out:$out\DSPCalculatorFlat.dll") + ($refs | ForEach-Object { "/reference:$_" })
if ($defines.Count -gt 0) { $cscArgs += "/define:" + ($defines -join ';') }
$cscArgs += $sources
# a freshly written dll is often held for a moment by the virus scanner: wait until it can be replaced
$dll = "$out\DSPCalculatorFlat.dll"
for ($i = 0; $i -lt 20 -and (Test-Path $dll); $i++) {
    try { Remove-Item $dll -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 250 }
}
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $out\DSPCalculatorFlat.dll ($mode engine)"

if ($Install) {
    $dest = Join-Path $plugins "ovitente-DSPCalculatorFlat"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item "$out\DSPCalculatorFlat.dll", (Join-Path $PSScriptRoot "manifest.json"), (Join-Path $PSScriptRoot "README.md") $dest -Force
    if ($mode -eq "dll") { Copy-Item $engineDll $dest -Force }
    Write-Host "Installed to $dest"
}
