# Builds the in-game overlay and gathers it into dist\ErRandoCompanion, ready to copy into the randomizer folder.
# Usage: powershell -File tools\package_mod.ps1 [-Install "<randomizer folder>"]
param([string]$Install)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# NativeAOT links with the Visual Studio tools, which it finds through vswhere.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:USERPROFILE\.cargo\bin;$env:PATH"

dotnet publish "$root\Native" -c Release -r win-x64 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Building the tracker DLL failed." }
cargo build --release --manifest-path "$root\mod\Cargo.toml" | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Building the overlay DLL failed." }

$out = "$root\dist\ErRandoCompanion"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force "$out\Data" | Out-Null
Copy-Item "$root\mod\target\release\ErRandoCompanionMod.dll" $out
Copy-Item "$root\Native\bin\Release\net8.0\win-x64\publish\ErRandoCompanion.Native.dll" $out
Copy-Item "$root\mod\README.md", "$root\LICENSE", "$root\THIRD_PARTY_NOTICES.md" $out
Copy-Item "$root\licenses" "$out\licenses" -Recurse
# The map pictures are FromSoftware's art and the overlay does not use them, so they stay out.
Get-ChildItem "$root\Data\*" -File -Exclude *.jpg | Copy-Item -Destination "$out\Data"
if (-not (Test-Path "$out\Data\locations.json")) { throw "The data files were not copied." }
Compress-Archive -Path $out -DestinationPath "$root\dist\ErRandoCompanionMod.zip" -Force
Write-Host "Packaged $out"

if ($Install) {
    $target = Join-Path $Install "ErRandoCompanion"
    New-Item -ItemType Directory -Force "$target\Data" | Out-Null
    # Keep the player's own ini, window layout and log; replace only what the build makes.
    Copy-Item "$out\ErRandoCompanionMod.dll", "$out\ErRandoCompanion.Native.dll", "$out\README.md", "$out\LICENSE", "$out\THIRD_PARTY_NOTICES.md" $target -Force
    Copy-Item "$out\licenses" $target -Recurse -Force
    Copy-Item "$out\Data\*" "$target\Data" -Force
    Write-Host "Installed to $target"
}
