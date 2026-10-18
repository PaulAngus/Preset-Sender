[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
# Build preparation only: the published EXE supplies its own runtime API.
& (Join-Path $PSScriptRoot 'Initialize-SharedMidi.ps1') -PrepareBuild
