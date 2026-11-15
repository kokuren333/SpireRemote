# SpireBridge build + staging script (run on the PC).
# Builds Release and copies ONLY the mod dll + manifest into
#   <this script's directory>\dist\SpireBridge\
# It NEVER touches the game's mods folder.
param(
    [string]$Sts2GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
)

$ErrorActionPreference = "Stop"

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\SpireBridge\SpireBridge.csproj"
$outDir  = Join-Path $root "src\SpireBridge\bin\Release\net9.0"
$distDir = Join-Path $root "dist\SpireBridge"

Write-Host "Building SpireBridge (Release)..."
dotnet build $project -c Release -p:Sts2GameDir="$Sts2GameDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Copy-Item (Join-Path $outDir "SpireBridge.dll") $distDir -Force
# PDB is useful for debugging during development; harmless in staging.
$pdb = Join-Path $outDir "SpireBridge.pdb"
if (Test-Path $pdb) { Copy-Item $pdb $distDir -Force }
Copy-Item (Join-Path $root "SpireBridge.json") $distDir -Force

Write-Host "Staged to $distDir :"
Get-ChildItem $distDir | ForEach-Object { Write-Host "  $($_.Name)  $($_.Length) bytes" }
