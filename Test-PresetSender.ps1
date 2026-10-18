#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('All', 'Functional', 'Native', 'Desktop')]
    [string]$Suite = 'All',
    [string]$ResultsDirectory,
    [string]$LoopbackPort
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = $PSScriptRoot
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
if (!$ResultsDirectory) { $ResultsDirectory = Join-Path $repo "artifacts/test-pilot/$runId" }
$resultRoot = [IO.Path]::GetFullPath($ResultsDirectory)
if (Test-Path -LiteralPath $resultRoot) { throw "Results directory already exists: $resultRoot. Choose a new directory to retain prior evidence." }
if ($LoopbackPort -and !$LoopbackPort.StartsWith('PresetSender-Test', [StringComparison]::Ordinal)) {
    throw 'Use a dedicated loopback port named PresetSender-Test...; the pilot does not select a physical device.'
}
$lockRoot = Join-Path $repo 'artifacts/test-pilot'
[IO.Directory]::CreateDirectory($lockRoot) | Out-Null
$lock = [IO.File]::Open((Join-Path $lockRoot 'runner.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
[IO.Directory]::CreateDirectory($resultRoot) | Out-Null
$buildRoot = Join-Path $resultRoot 'build'
$report = [ordered]@{
    schemaVersion = 1; app = 'Preset Sender'; runId = $runId; suite = $Suite
    startedUtc = [DateTime]::UtcNow.ToString('o'); finishedUtc = $null; status = 'running'
    machine = [Environment]::MachineName
    os = if ($IsWindows) { $(if ([Environment]::OSVersion.Version.Build -ge 22000) { 'Windows 11' } else { 'Windows 10' }) + ' (build ' + [Environment]::OSVersion.Version.Build + ')' } else { [Runtime.InteropServices.RuntimeInformation]::OSDescription }
    osVersion = [Environment]::OSVersion.Version.ToString()
    architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    powershell = $PSVersionTable.PSVersion.ToString(); dotnet = $null
    source = $null; midiPackageVersion = $null; executableSha256 = $null; capabilities = $null
    stages = [Collections.Generic.List[object]]::new()
    limits = @('Physical MIDI device behaviour is not tested.', 'Desktop suite uses a tool-launched private executable, not Explorer/Finder.', 'Other machines and OS versions require their own runs.')
}
$childEnvironment = @{
    PRESET_SENDER_DATA_DIRECTORY = (Join-Path $resultRoot 'profile')
    PRESET_SENDER_SCREENSHOT_DIR = (Join-Path $resultRoot 'screenshots/main')
    PRESET_SENDER_CONFIG_SCREENSHOT_DIR = (Join-Path $resultRoot 'screenshots/configuration')
    PRESET_SENDER_NATIVE_MIDI_TEST = $null
    PRESET_SENDER_TEST_EXE = $null
    PRESET_SENDER_VIRTUAL_DEVICE_NAME = $null
    MBU_MIDI_BACKEND = $null; MBU_MIDI_API_DIRECTORY = $null; MIDI_FORWARDER_MIDI_API = $null
    DOTNET_BUNDLE_EXTRACT_BASE_DIR = (Join-Path $resultRoot 'bundle-cache')
}

function Invoke-TestCommand {
    param([string]$Name, [string]$Command, [string[]]$Arguments, [int]$TimeoutSeconds = 180, [hashtable]$ExtraEnvironment = @{})
    $start = [Diagnostics.ProcessStartInfo]::new($Command)
    $start.WorkingDirectory = $repo; $start.UseShellExecute = $false
    $start.CreateNoWindow = $true; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $variables = @{} + $childEnvironment
    foreach ($key in $ExtraEnvironment.Keys) { $variables[$key] = $ExtraEnvironment[$key] }
    foreach ($key in $variables.Keys) {
        if ($null -eq $variables[$key]) { [void]$start.Environment.Remove($key) }
        else { $start.Environment[$key] = [string]$variables[$key] }
    }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    $stdoutTask = $null; $stderrTask = $null; $timedOut = $false; $started = $false
    try {
        $started = $process.Start()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync(); $stderrTask = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true; $process.Kill($true); [void]$process.WaitForExit(5000)
        }
        $stdout = $stdoutTask.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
        $stderr = $stderrTask.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $resultRoot "$Name.stdout.txt"), $stdout)
        [IO.File]::WriteAllText((Join-Path $resultRoot "$Name.stderr.txt"), $stderr)
        if ($timedOut) { throw "$Name exceeded $TimeoutSeconds seconds; its process tree was terminated." }
        if ($process.ExitCode -ne 0) { throw "$Name failed with exit code $($process.ExitCode). See $Name.stdout.txt and $Name.stderr.txt." }
        return $stdout
    }
    finally {
        if ($started -and !$process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

function Invoke-Stage {
    param([string]$Name, [scriptblock]$Action)
    $stage = [ordered]@{ name = $Name; status = 'running'; seconds = 0; details = $null }
    $report.stages.Add($stage)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "[$Name] Running..."
    try {
        $stage.details = & $Action
        $stage.status = 'passed'
        if ($stage.details -and $stage.details.PSObject.Properties['status'] -and $stage.details.status -eq 'unavailable') { $stage.status = 'unavailable' }
    }
    catch { $stage.status = 'failed'; $stage.details = [pscustomobject]@{ error = $_.Exception.Message }; throw }
    finally { $stage.seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2); Write-Host "[$Name] $($stage.status) ($($stage.seconds)s)" }
}

function Read-TestResults {
    param([string]$Directory)
    $files = @(Get-ChildItem -LiteralPath $Directory -Filter '*.trx')
    if ($files.Count -ne 1) { throw "Expected one TRX result in $Directory, found $($files.Count)." }
    [xml]$trx = [IO.File]::ReadAllText($files[0].FullName)
    $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
    if (!$counters -or [int]$counters.total -eq 0) { throw 'Test run did not discover any tests.' }
    if ([int]$counters.failed -gt 0) { throw 'Test results contain failures.' }
    return [pscustomobject]@{
        total = [int]$counters.total; passed = [int]$counters.passed; failed = [int]$counters.failed
        skipped = [int]$counters.notExecuted; trx = [IO.Path]::GetRelativePath($resultRoot, $files[0].FullName)
    }
}

function Write-Report {
    $report.finishedUtc = [DateTime]::UtcNow.ToString('o')
    [IO.File]::WriteAllText((Join-Path $resultRoot 'report.json'), ($report | ConvertTo-Json -Depth 12))
    $encode = { param($value) [Net.WebUtility]::HtmlEncode([string]$value) }
    $rows = foreach ($stage in $report.stages) {
        $detail = if ($stage.details) { $stage.details | ConvertTo-Json -Depth 8 } else { '' }
        '<tr><td>' + (& $encode $stage.name) + '</td><td>' + (& $encode $stage.status) + '</td><td>' + $stage.seconds + '</td><td><pre>' + (& $encode $detail) + '</pre></td></tr>'
    }
    $pictures = foreach ($picture in @(Get-ChildItem -LiteralPath $resultRoot -Filter '*.png' -Recurse | Where-Object { $_.FullName -notlike "$buildRoot*" -and $_.FullName -notlike '*bundle-cache*' })) {
        $relative = [IO.Path]::GetRelativePath($resultRoot, $picture.FullName).Replace('\', '/')
        '<a href="' + (& $encode $relative) + '">' + (& $encode $relative) + '</a><br>'
    }
    $html = '<!doctype html><html lang="en"><meta charset="utf-8"><title>Preset Sender test report</title><style>body{font:16px system-ui;max-width:1200px;margin:32px auto;padding:0 20px;color:#17232d}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccd3d9;padding:12px;text-align:left;vertical-align:top}pre{white-space:pre-wrap;margin:0;font-size:13px}h1{font-size:26px}</style><h1>Preset Sender: ' + (& $encode $report.status) + '</h1><p>' + (& $encode $report.os) + ' / ' + (& $encode $report.architecture) + ' / ' + (& $encode $report.runId) + '</p><p><a href="report.json">Machine-readable report</a> · <a href="source.json">Source identities</a></p><table><tr><th>Stage</th><th>Status</th><th>Seconds</th><th>Evidence</th></tr>' + ($rows -join '') + '</table><h2>Coverage limits</h2><ul>' + (($report.limits | ForEach-Object { '<li>' + (& $encode $_) + '</li>' }) -join '') + '</ul><h2>Screenshots</h2>' + ($pictures -join '') + '</html>'
    [IO.File]::WriteAllText((Join-Path $resultRoot 'report.html'), $html)
}

$exitCode = 1
try {
    Invoke-Stage 'source-and-sdk' {
        $report.dotnet = (Invoke-TestCommand 'sdk' 'dotnet' @('--version')).Trim()
        if ($IsMacOS) { $report.os = 'macOS ' + (Invoke-TestCommand 'os-version' '/usr/bin/sw_vers' @('-productVersion')).Trim() }
        $head = (& git -C $repo rev-parse HEAD 2>$null)
        $gitStatus = @(& git -C $repo status --porcelain)
        $sourceFiles = @(& git -C $repo ls-files --cached --others --exclude-standard) | Sort-Object -Unique
        $hashes = foreach ($relative in $sourceFiles) {
            $path = Join-Path $repo $relative
            [pscustomobject]@{ path = $relative; sha256 = if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { $null } }
        }
        [IO.File]::WriteAllText((Join-Path $resultRoot 'source.json'), (@{ head = $head; workingTree = $gitStatus; files = @($hashes) } | ConvertTo-Json -Depth 6))
        $report.source = [pscustomobject]@{ head = $head; dirty = $gitStatus.Count -gt 0; manifestSha256 = (Get-FileHash -LiteralPath (Join-Path $resultRoot 'source.json')).Hash }
        [xml]$midiVersion = [IO.File]::ReadAllText((Join-Path $repo 'dependencies/midi/MidiVersion.props'))
        $report.midiPackageVersion = $midiVersion.Project.PropertyGroup.SharedMidiVersion
        [pscustomobject]@{ sdk = $report.dotnet; revision = $head; dirty = $report.source.dirty; midiPackage = $report.midiPackageVersion }
    }
    if ($IsWindows) {
        Invoke-Stage 'windows-midi-build-feed' {
            [pscustomobject]@{ preparation = (Invoke-TestCommand 'midi-build-feed' 'pwsh' @('-NoProfile', '-File', (Join-Path $repo 'Initialize-MidiSdk.ps1'))).Trim() }
        }
    }
    if ($Suite -in @('All', 'Functional')) {
        Invoke-Stage 'functional' {
            $trxDirectory = Join-Path $resultRoot 'functional'
            [void](Invoke-TestCommand 'functional' 'dotnet' @('test', 'Tests/PresetSender.Tests.csproj', '-c', 'Release', '--artifacts-path', $buildRoot, '--filter', 'FullyQualifiedName!~VirtualMidiIntegrationTests', '--logger', 'trx', '--results-directory', $trxDirectory))
            $data = Read-TestResults $trxDirectory
            if ($data.skipped -gt 0) { throw 'Functional suite unexpectedly skipped tests.' }
            $data
        }
    }
    if ($Suite -ne 'Functional') {
        Invoke-Stage 'package-and-diagnostics' {
            $script:packageDirectory = Join-Path $resultRoot 'package'
            $rid = if ($IsWindows) { 'win-x64' } elseif ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'osx-arm64' } else { 'osx-x64' }
            $publishArguments = @('publish', 'PresetSender.csproj', '-c', 'Release', '-r', $rid, '--self-contained', 'true', '--artifacts-path', $buildRoot, '-o', $packageDirectory)
            if ($IsWindows) { $publishArguments += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:IncludeAllContentForSelfExtract=true', '-p:EnableCompressionInSingleFile=true') }
            [void](Invoke-TestCommand 'publish' 'dotnet' $publishArguments)
            $script:executable = Join-Path $packageDirectory $(if ($IsWindows) { 'PresetSender.exe' } else { 'PresetSender' })
            $report.executableSha256 = (Get-FileHash -LiteralPath $executable).Hash
            $diagnostics = Invoke-TestCommand 'diagnostics' $executable @('--diagnostics-json') 45
            $report.capabilities = $diagnostics | ConvertFrom-Json
            [pscustomobject]@{ executable = $executable; sha256 = $report.executableSha256; capabilities = $report.capabilities }
        }
        if ($Suite -in @('All', 'Native')) {
            Invoke-Stage 'native-midi' {
                if ($IsWindows -and $report.capabilities.canCreateVirtualPorts -and !$LoopbackPort) {
                    $trxDirectory = Join-Path $resultRoot 'native'
                    [void](Invoke-TestCommand 'native' 'dotnet' @('test', 'Tests/PresetSender.Tests.csproj', '-c', 'Release', '--artifacts-path', $buildRoot, '--filter', 'FullyQualifiedName~VirtualMidiIntegrationTests', '--logger', 'trx', '--results-directory', $trxDirectory) 180 @{ PRESET_SENDER_NATIVE_MIDI_TEST = '1'; PRESET_SENDER_TEST_EXE = $executable })
                    $data = Read-TestResults $trxDirectory
                    if ($data.skipped -gt 0 -or $data.passed -lt 3) { throw 'Required native tests were skipped or not discovered.' }
                    $data
                }
                elseif ($LoopbackPort) {
                    [void](Invoke-TestCommand 'probe-build' 'dotnet' @('build', 'tools/PresetSender.Pilot/PresetSender.Pilot.csproj', '-c', 'Release', '--artifacts-path', $buildRoot))
                    $platform = if ($IsWindows) { 'Windows' } else { 'macOS' }
                    $probe = Join-Path $buildRoot "bin/PresetSender.Pilot/$platform/release/PresetSender.Pilot.dll"
                    [void](Invoke-TestCommand 'loopback' 'dotnet' @($probe, 'loopback', '--port', $LoopbackPort, '--result', (Join-Path $resultRoot 'loopback.json')))
                    Get-Content -LiteralPath (Join-Path $resultRoot 'loopback.json') -Raw | ConvertFrom-Json
                }
                else { [pscustomobject]@{ status = 'unavailable'; reason = 'Built-in virtual MIDI unavailable. Configure a dedicated PresetSender-Test loopback port and pass -LoopbackPort.' } }
            }
        }
        if ($Suite -in @('All', 'Desktop')) {
            Invoke-Stage 'packaged-desktop' {
                if (!$IsWindows) { [pscustomobject]@{ status = 'unavailable'; reason = 'Mac desktop automation adapter is not part of this Windows pilot.' }; return }
                if (!$report.capabilities.canCreateVirtualPorts -and !$LoopbackPort) { [pscustomobject]@{ status = 'unavailable'; reason = 'Configure a dedicated test loopback port before desktop testing.' }; return }
                [void](Invoke-TestCommand 'desktop-build' 'dotnet' @('build', 'tools/PresetSender.Pilot/PresetSender.Pilot.csproj', '-c', 'Release', '--artifacts-path', $buildRoot))
                $probe = Join-Path $buildRoot 'bin/PresetSender.Pilot/Windows/release/PresetSender.Pilot.dll'
                $desktopDirectory = Join-Path $resultRoot 'desktop'
                $port = if ($LoopbackPort) { $LoopbackPort } else { 'PresetSender-Test-' + [Guid]::NewGuid().ToString('N').Substring(0, 12) }
                $useVirtual = if ($LoopbackPort) { 'false' } else { 'true' }
                [void](Invoke-TestCommand 'desktop' 'dotnet' @($probe, 'desktop', '--exe', $executable, '--directory', $desktopDirectory, '--port', $port, '--virtual', $useVirtual) 180)
                Get-Content -LiteralPath (Join-Path $desktopDirectory 'desktop.json') -Raw | ConvertFrom-Json
            }
        }
    }
    $report.status = if (@($report.stages | Where-Object { $_.status -eq 'unavailable' }).Count) { 'incomplete' } else { 'passed' }
    $exitCode = if ($report.status -eq 'passed') { 0 } else { 2 }
}
catch { $report.status = 'failed'; Write-Warning $_.Exception.Message }
finally {
    try { Write-Report; Write-Host "Report: $(Join-Path $resultRoot 'report.html')" }
    finally { $lock.Dispose() }
}
exit $exitCode
