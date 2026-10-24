[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/windows'),
    [switch]$Sign,
    [string]$SigningCertificateThumbprint = $env:MBU_WINDOWS_SIGNING_THUMBPRINT,
    [string]$SignToolPath = $env:MBU_SIGNTOOL_PATH,
    [uri]$TimestampServer = 'http://time.certum.pl'
)

$ErrorActionPreference = 'Stop'
$taskCommit = (& git -C $PSScriptRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or @(& git -C $PSScriptRoot status --porcelain).Count) { throw 'Publish Preset Sender from clean committed source.' }
$signScript = Join-Path $PSScriptRoot 'dependencies/midi/Sign-WindowsExecutable.ps1'
$signing = @{ Sign = $Sign; SigningCertificateThumbprint = $SigningCertificateThumbprint; SignToolPath = $SignToolPath; TimestampServer = $TimestampServer }
& $signScript @signing -CheckOnly
& (Join-Path $PSScriptRoot 'Initialize-MidiSdk.ps1')
$taskProject = Join-Path $PSScriptRoot 'PresetSender.csproj'
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$taskBuild = Join-Path ([IO.Path]::GetTempPath()) ('sender-publish-' + [guid]::NewGuid().ToString('N'))
$taskVersion = ([xml](Get-Content -LiteralPath $taskProject -Raw)).SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
if (Test-Path -LiteralPath (Join-Path $taskOutput "Preset-Sender-$taskVersion-win-x64.zip")) { throw 'Preserve existing release bytes; select a new version or fresh publication directory.' }
$taskInputs = [ordered]@{}
foreach ($taskName in (& git -C $PSScriptRoot -c core.quotepath=false ls-files)) {
    $taskInputs[$taskName] = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $taskName)).Hash.ToLowerInvariant()
}
dotnet publish $taskProject -c Release -r win-x64 --self-contained true --artifacts-path $taskBuild '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:IncludeAllContentForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' -o $taskOutput
if ($LASTEXITCODE -ne 0) { throw "Preset Sender publish failed with exit code $LASTEXITCODE." }
& $signScript @signing -ExecutablePath (Join-Path $taskOutput 'PresetSender.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $taskOutput 'README.md')
& (Join-Path $PSScriptRoot 'dependencies/midi/New-WindowsReleaseArchive.ps1') `
    -ExecutablePath (Join-Path $taskOutput 'PresetSender.exe') -Name 'Preset-Sender' -OutputDirectory $taskOutput `
    -NoticePath @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'dependencies/midi') -File |
        Where-Object Name -Match 'LICENSE|NOTICES' | ForEach-Object FullName)
Write-Output (Join-Path $taskOutput 'PresetSender.exe')
if (@(& git -C $PSScriptRoot status --porcelain).Count) { throw 'Source changed during publication.' }
foreach ($taskName in $taskInputs.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $taskName)).Hash.ToLowerInvariant() -ne $taskInputs[$taskName]) { throw "Publication source changed: $taskName" }
}
$taskSourcePath = Join-Path $taskOutput 'source-receipt.json'
[ordered]@{ projectId='preset-sender'; version=$taskVersion; sourceCommit=$taskCommit; gitTree=(& git -C $PSScriptRoot rev-parse 'HEAD^{tree}').Trim(); workingTreeClean=$true; dirty=$false; sourceFiles=$taskInputs } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskSourcePath -Encoding utf8NoBOM
$taskBundle = Join-Path $taskOutput "Preset-Sender-$taskVersion-source.bundle"
& git -C $PSScriptRoot bundle create $taskBundle HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not retain normal publication source bundle.' }
$taskZip = Join-Path $taskOutput "Preset-Sender-$taskVersion-win-x64.zip"
$taskExe = Join-Path $taskOutput 'PresetSender.exe'
$taskFiles = [ordered]@{}
$taskArchive = [IO.Compression.ZipFile]::OpenRead($taskZip)
try {
    foreach ($taskEntry in $taskArchive.Entries | Where-Object Name) {
        $taskStream = $taskEntry.Open()
        try { $taskFiles[$taskEntry.FullName] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskStream)).ToLowerInvariant() }
        finally { $taskStream.Dispose() }
    }
} finally { $taskArchive.Dispose() }
$taskPinVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies/midi/version.txt') -Raw).Trim()
$taskLibraries = [ordered]@{}
foreach ($taskLine in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies/midi/packages.sha256')) {
    $taskParts = $taskLine.Trim() -split '\s+',2
    if ($taskParts.Length -eq 2) { $taskLibraries[$taskParts[1]] = $taskParts[0].ToLowerInvariant() }
}
[ordered]@{ applicationId='preset-sender'; version=$taskVersion; sourceCommit=$taskCommit; workingTreeCleanBefore=$true; workingTreeCleanAfter=$true; normalPublication=$true; runtimeIdentifier='win-x64'; configuration='Release'; selfContained=$true; publishSingleFile=$true; includeAllContentForSelfExtract=$true; enableCompressionInSingleFile=$true; buildDirectory=$taskBuild; binary=@{path=$taskExe;sha256=(Get-FileHash -LiteralPath $taskExe).Hash.ToLowerInvariant()}; package=@{path=$taskZip;sha256=(Get-FileHash -LiteralPath $taskZip).Hash.ToLowerInvariant()}; packageFiles=$taskFiles; sourceIdentity=@{path=$taskSourcePath;sha256=(Get-FileHash -LiteralPath $taskSourcePath).Hash.ToLowerInvariant()}; sourceBundle=@{path=$taskBundle;sha256=(Get-FileHash -LiteralPath $taskBundle).Hash.ToLowerInvariant()}; midiVersion=$taskPinVersion; libraryPackageHashes=$taskLibraries; librarySourceManifestSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'dependencies/midi/source-manifest.json')).Hash.ToLowerInvariant(); testsExecuted=$false; applicationLaunched=$false; releaseQualified=$false; publishedUtc=[DateTime]::UtcNow.ToString('o') } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskOutput 'normal-publication.json') -Encoding utf8NoBOM
