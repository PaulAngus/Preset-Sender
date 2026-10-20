[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/windows'),
    [switch]$Sign,
    [string]$SigningCertificateThumbprint = $env:MBU_WINDOWS_SIGNING_THUMBPRINT,
    [string]$SignToolPath = $env:MBU_SIGNTOOL_PATH,
    [uri]$TimestampServer = 'http://time.certum.pl'
)

$ErrorActionPreference = 'Stop'
$signScript = Join-Path $PSScriptRoot 'dependencies/midi/Sign-WindowsExecutable.ps1'
$signing = @{ Sign = $Sign; SigningCertificateThumbprint = $SigningCertificateThumbprint; SignToolPath = $SignToolPath; TimestampServer = $TimestampServer }
& $signScript @signing -CheckOnly
& (Join-Path $PSScriptRoot 'Initialize-MidiSdk.ps1')
$taskProject = Join-Path $PSScriptRoot 'PresetSender.csproj'
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$taskBuild = Join-Path $PSScriptRoot ('artifacts/publish-' + [guid]::NewGuid().ToString('N'))
dotnet publish $taskProject -c Release -r win-x64 --self-contained true --artifacts-path $taskBuild '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:IncludeAllContentForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' -o $taskOutput
if ($LASTEXITCODE -ne 0) { throw "Preset Sender publish failed with exit code $LASTEXITCODE." }
& $signScript @signing -ExecutablePath (Join-Path $taskOutput 'PresetSender.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $taskOutput 'README.md')
Write-Output (Join-Path $taskOutput 'PresetSender.exe')
