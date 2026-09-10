param()
$ErrorActionPreference = 'Stop'
$calendarRoot = $PSScriptRoot
$toolDirectory = Join-Path $calendarRoot '.tools'
$packageDirectory = Join-Path $calendarRoot '.packages'
$nugetPath = Join-Path $toolDirectory 'nuget.exe'

New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $packageDirectory | Out-Null

if (!(Test-Path -LiteralPath $nugetPath)) {
    $temporary = $nugetPath + '.tmp'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile $temporary
    Move-Item -LiteralPath $temporary -Destination $nugetPath
}

& $nugetPath install (Join-Path $calendarRoot 'packages.config') -OutputDirectory $packageDirectory -NonInteractive -DirectDownload
if ($LASTEXITCODE -ne 0) { throw '邮件依赖恢复失败。' }

Write-Output ('邮件依赖已恢复到：' + $packageDirectory)
