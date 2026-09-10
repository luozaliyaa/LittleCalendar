param([switch]$Test, [string]$OutputDirectory = 'release')
$ErrorActionPreference = 'Stop'
$calendarRoot = $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (!(Test-Path -LiteralPath $compilerPath)) { throw '需要 Windows .NET Framework 4.x。' }
$outputPath = Join-Path $calendarRoot $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$references = @('System.dll', 'System.Core.dll', 'System.Security.dll', 'System.Web.Extensions.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll', 'System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$packageRoot = Join-Path $calendarRoot '.packages'
$mailKitPath = Join-Path $packageRoot 'MailKit.4.17.0\lib\net462\MailKit.dll'
$mimeKitPath = Join-Path $packageRoot 'MimeKit.4.17.0\lib\net462\MimeKit.dll'
$requiredPackageFiles = @(
  $mailKitPath,
  $mimeKitPath,
  (Join-Path $packageRoot 'BouncyCastle.Cryptography.2.6.2\lib\net461\BouncyCastle.Cryptography.dll'),
  (Join-Path $packageRoot 'System.Buffers.4.6.1\lib\net462\System.Buffers.dll'),
  (Join-Path $packageRoot 'System.Formats.Asn1.8.0.1\lib\net462\System.Formats.Asn1.dll'),
  (Join-Path $packageRoot 'System.Memory.4.6.3\lib\net462\System.Memory.dll'),
  (Join-Path $packageRoot 'System.Runtime.CompilerServices.Unsafe.6.1.2\lib\net462\System.Runtime.CompilerServices.Unsafe.dll'),
  (Join-Path $packageRoot 'System.Threading.Tasks.Extensions.4.6.3\lib\net462\System.Threading.Tasks.Extensions.dll')
)
if ($requiredPackageFiles.Where({ !(Test-Path -LiteralPath $_) }).Count -gt 0) {
  throw '缺少邮件依赖。请先运行 windows-calendar\restore-packages.ps1。'
}
$packageDlls = Get-ChildItem -LiteralPath $packageRoot -Recurse -Filter '*.dll' |
  Where-Object { $_.FullName -match '\\lib\\(net462|net461|net46|netstandard2\.0)\\[^\\]+\.dll$' } |
  Group-Object Name | ForEach-Object {
    $_.Group | Sort-Object @{ Expression = {
      if ($_.FullName -match '\\lib\\net462\\') { 0 }
      elseif ($_.FullName -match '\\lib\\net461\\') { 1 }
      elseif ($_.FullName -match '\\lib\\net46\\') { 2 }
      else { 3 }
    }} | Select-Object -First 1
  }
$packageReferences = $packageDlls | ForEach-Object { '/reference:' + $_.FullName }
$sourceFiles = @('MailModels.cs', 'MailStore.cs', 'MailDiagnostics.cs', 'MailClient.cs', 'MailAnalysis.cs', 'MailSync.cs', 'ChatModels.cs', 'ChatStore.cs', 'Model.cs', 'Deadlines.cs', 'ChatAssistant.cs', 'CalendarVisuals.cs', 'DeadlineFields.cs', 'Agent.cs', 'Desktop.cs', 'Program.cs') | ForEach-Object { Join-Path $calendarRoot $_ }
& $compilerPath /nologo /utf8output /target:winexe /optimize+ /platform:anycpu ('/out:' + (Join-Path $outputPath 'LittleCalendar.exe')) ('/win32manifest:' + (Join-Path $calendarRoot 'app.manifest')) ('/resource:' + (Join-Path $calendarRoot 'Theme.xaml') + ',Theme.xaml') @references @packageReferences @sourceFiles
if ($LASTEXITCODE -ne 0) { throw '日历程序构建失败。' }
$packageDlls | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $outputPath $_.Name) -Force }
Copy-Item -LiteralPath (Join-Path $calendarRoot 'README.md') -Destination (Join-Path $outputPath '使用说明.md') -Force
Write-Output ('已生成：' + (Join-Path $outputPath 'LittleCalendar.exe'))
if ($Test) {
  & $compilerPath /nologo /utf8output /target:exe /optimize+ ('/out:' + (Join-Path $outputPath 'CalendarTests.exe')) ('/reference:' + (Join-Path $outputPath 'LittleCalendar.exe')) @references @packageReferences (Join-Path $calendarRoot 'Tests.cs')
  if ($LASTEXITCODE -ne 0) { throw '测试构建失败。' }
  & (Join-Path $outputPath 'CalendarTests.exe')
  if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
}
