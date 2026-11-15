param(
    [string] $SPTPath = 'F:\SPT',
    [switch] $NoInstall
)

# Builds TushonkaHorse.dll against the SPT install, packs dist\TushonkaHorse-<version>.zip and copies it into
# BepInEx\plugins\TushonkaHorse. The assets (assets\) aren't in the repo; they come with the release zip.
$ErrorActionPreference = 'Stop'
$name = 'TushonkaHorse'
$root = $PSScriptRoot
$project = Join-Path $root "$name.csproj"
$fikaProject = Join-Path $root "Fika\$name.Fika.csproj"

if (-not (Test-Path (Join-Path $SPTPath 'BepInEx\core\BepInEx.dll'))) {
    throw "$SPTPath doesn't look like an SPT folder (no BepInEx\core\BepInEx.dll)."
}
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$pluginVersion = (Select-String -Path (Join-Path $root 'Plugin.cs') -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
if ($version -ne $pluginVersion) { throw "csproj says $version but Plugin.cs says $pluginVersion." }

$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dotnet = if (Test-Path $userDotnet) { $userDotnet } else { 'dotnet' }
& $dotnet build $project -c Release "-p:SPTPath=$SPTPath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if (Test-Path (Join-Path $SPTPath 'BepInEx\plugins\Fika\Fika.Core.dll')) {
    & $dotnet build $fikaProject -c Release "-p:SPTPath=$SPTPath" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Fika build failed.' }
}
else { throw "$SPTPath has no Fika.Core.dll; Fika/headless support cannot be built." }

$assets = 'horse.bin', 'coat_darkbrown.jpg', 'coat_lightbrown.jpg', 'coat_spotted.jpg', 'coat_brown.jpg', 'coat_n.png',
          'mane_black.png', 'mane_brown.png', 'mane_white.png', 'eye.jpg', 'saddle.jpg', 'reins.jpg', 'rider.bin'
$files = @((Join-Path $root "bin\Release\$name.dll"), (Join-Path $root "Fika\bin\Release\$name.Fika.dll"), (Join-Path $root 'README.md'))
foreach ($a in $assets) { $files += (Join-Path $root "assets\$a") }
foreach ($f in $files) { if (-not (Test-Path $f)) { throw "Missing $f" } }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "$name-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($f in $files) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f, "BepInEx/plugins/$name/" + (Split-Path $f -Leaf)) | Out-Null
    }
}
finally { $archive.Dispose() }
Write-Host "Packed $zip"

if (-not $NoInstall) {
    $dest = Join-Path $SPTPath "BepInEx\plugins\$name"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $files $dest -Force
    Write-Host "Installed to $dest"
}
