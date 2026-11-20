param([Parameter(Mandatory = $true)][string] $Runtime)
$ErrorActionPreference = 'Stop'
if ($Runtime -notin @('win-x64', 'win-arm64')) { throw 'Use build.sh for macOS/Linux bundles; this script packages Windows.' }
$projectRoot = $PSScriptRoot
$version = ([xml](Get-Content -Raw (Join-Path $projectRoot 'Directory.Build.props'))).Project.PropertyGroup.VersionPrefix
$output = Join-Path $projectRoot "dist/$Runtime"
dotnet publish (Join-Path $projectRoot 'src/SiennaEditor/SiennaEditor.csproj') -c Release -r $Runtime --self-contained true -o $output `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Sienna Editor publish failed.' }
$archive = Join-Path $projectRoot "dist/Sienna Editor-$version-$Runtime.zip"
$packageFiles = 'Sienna Editor.exe', 'data', 'LICENSE', 'LICENSE-MATERIAL-DESIGN-ICONS.txt'
Compress-Archive -LiteralPath ($packageFiles | ForEach-Object { Join-Path $output $_ }) -DestinationPath $archive -Force
Write-Output "Created $archive"
