param(
    [Parameter(Mandatory = $true)]
    [string]$InputDirectory,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$resolvedInput = (Resolve-Path -LiteralPath $InputDirectory).Path
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'
if (!$resolvedInput.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'InputDirectory must be inside this repository.'
}

$application = Join-Path $resolvedInput 'LittleCalendar.exe'
if (!(Test-Path -LiteralPath $application -PathType Leaf)) {
    throw 'LittleCalendar.exe was not found in InputDirectory.'
}

$runtimeDlls = @(Get-ChildItem -LiteralPath $resolvedInput -File -Filter '*.dll')
$readme = Get-ChildItem -LiteralPath $resolvedInput -File -Filter '*.md' | Select-Object -First 1
if ($null -eq $readme) {
    throw 'A Markdown readme was not found in InputDirectory.'
}

$outputCandidate = $OutputDirectory
if (![IO.Path]::IsPathRooted($outputCandidate)) {
    $outputCandidate = Join-Path (Get-Location).Path $outputCandidate
}
$resolvedOutput = [IO.Path]::GetFullPath($outputCandidate)
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
$staging = Join-Path $resolvedOutput ('package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null

try {
    Copy-Item -LiteralPath $application -Destination $staging
    Copy-Item -LiteralPath $readme.FullName -Destination (Join-Path $staging 'README.md')
    foreach ($dll in $runtimeDlls) {
        Copy-Item -LiteralPath $dll.FullName -Destination $staging
    }

    $stagedNames = @(Get-ChildItem -LiteralPath $staging -File | ForEach-Object { $_.Name })
    if ($stagedNames -contains 'CalendarTests.exe') {
        throw 'Test executable must not be included in a release package.'
    }
    if ($stagedNames.Where({ $_ -match '(?i)(\.log|calendar\.json|mail-sync\.json|-key\.dat)$' }).Count -gt 0) {
        throw 'Release package contains a forbidden user-data file.'
    }

    $archive = Join-Path $resolvedOutput 'LittleCalendar-Windows-x64.zip'
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -CompressionLevel Optimal

    $archiveEntries = @(
        Add-Type -AssemblyName System.IO.Compression.FileSystem -PassThru | Out-Null
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try { $zip.Entries | ForEach-Object { $_.FullName } } finally { $zip.Dispose() }
    )
    if (!($archiveEntries -contains 'LittleCalendar.exe') -or $archiveEntries -contains 'CalendarTests.exe') {
        throw 'Release archive content verification failed.'
    }

    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    Write-Output ('Package: ' + $archive)
    Write-Output ('SHA256: ' + $hash)
}
finally {
    if (Test-Path -LiteralPath $staging) {
        $resolvedStaging = (Resolve-Path -LiteralPath $staging).Path
        $outputPrefix = $resolvedOutput.TrimEnd('\') + '\'
        if ($resolvedStaging.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
        }
    }
}
