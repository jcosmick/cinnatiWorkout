# Crea l'installer MSI in .\dist\CinnatiWorkout-Setup.msi
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
# Cartella di appoggio separata: un'app avviata da .\publish non blocca la build.
$publishDir = "$root\build\publish"

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish "$root\src\CinnatiWorkout\CinnatiWorkout.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish non riuscito" }

dotnet build "$root\installer\CinnatiWorkout.Installer.wixproj" --no-incremental -c Release -o "$root\dist" "-p:PublishDir=$publishDir"
if ($LASTEXITCODE -ne 0) { throw "creazione installer non riuscita" }

Write-Host "Installer pronto: $root\dist\CinnatiWorkout-Setup.msi"
