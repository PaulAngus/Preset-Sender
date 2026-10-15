[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
# Compatibility entry point; legacy-only machines require no optional component.
if ([Environment]::OSVersion.Platform -eq 'Win32NT' -and [Environment]::OSVersion.Version -ge [version]'10.0.26200') {
    & (Join-Path $PSScriptRoot 'Initialize-SharedMidi.ps1') -EnableWindowsServices
}
