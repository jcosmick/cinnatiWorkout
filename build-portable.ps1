# Crea la versione portable in .\dist\CinnatiWorkout-Portable-<versione>.zip
# (un solo CinnatiWorkout.exe autonomo: non serve installare né .NET né altro, basta copiarlo e avviarlo).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = "$root\src\CinnatiWorkout\CinnatiWorkout.csproj"
# Cartella di appoggio separata: un'app avviata da .\publish non blocca la build.
$publishDir = "$root\build\portable"

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish non riuscito" }

New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$zip = "$root\dist\CinnatiWorkout-Portable-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$publishDir\CinnatiWorkout.exe" -DestinationPath $zip

Write-Host "Versione portable pronta: $zip"
