[CmdletBinding()]
param([ValidateSet('arm64','x64')][string]$Architecture = 'arm64', [string]$SigningIdentity = '-', [string]$NotaryProfile)
$ErrorActionPreference = 'Stop'
$version = ([xml](Get-Content (Join-Path $PSScriptRoot 'PresetSender.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ }
& (Join-Path $PSScriptRoot 'dependencies/midi/Publish-MacApplication.ps1') `
    -Project (Join-Path $PSScriptRoot 'PresetSender.csproj') -Executable 'PresetSender' `
    -Name 'Preset Sender' -Identifier 'com.mightbeuseful.presetsender' -Version $version `
    -Architecture $Architecture -OutputDirectory (Join-Path $PSScriptRoot 'artifacts/macos') `
    -SigningIdentity $SigningIdentity -NotaryProfile $NotaryProfile
