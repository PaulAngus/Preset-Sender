[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/windows'))

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Initialize-MidiSdk.ps1')
$taskProject = Join-Path $PSScriptRoot 'PresetSender.csproj'
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$taskBuild = Join-Path $PSScriptRoot ('artifacts/publish-' + [guid]::NewGuid().ToString('N'))
dotnet publish $taskProject -c Release -r win-x64 --self-contained true --artifacts-path $taskBuild '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:IncludeAllContentForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' -o $taskOutput
if ($LASTEXITCODE -ne 0) { throw "Preset Sender publish failed with exit code $LASTEXITCODE." }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $taskOutput 'README.md')
Write-Output (Join-Path $taskOutput 'PresetSender.exe')
