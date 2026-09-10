param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$forbiddenPathPattern = '^(\.packages[^/]*|\.tools|release[^/]*|test-[^/]*|test-results|check-deadline|artifacts)/'
$forbiddenFilePattern = '(?i)(\.exe|\.dll|\.zip|\.log|calendar\.json(\.bak)?|mail-sync\.json|[^/]*-key\.dat)$'
$secretPatterns = @(
    '(?i)\bsk-[A-Za-z0-9_-]{12,}\b',
    '(?i)\bBearer\s+[A-Za-z0-9._-]{12,}\b',
    '(?i)\b1[3-9]\d{9}@163\.com\b'
)
$textExtensions = @('.cs', '.md', '.ps1', '.yml', '.yaml', '.json', '.xml', '.xaml', '.gitignore')
$violations = New-Object System.Collections.Generic.List[string]

$trackedFiles = @(& git -C $repositoryRoot ls-files)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to read tracked Git files.'
}

foreach ($relativePath in $trackedFiles) {
    $normalized = $relativePath.Replace('\', '/')
    if ($normalized -match $forbiddenPathPattern) {
        $violations.Add('Forbidden directory: ' + $normalized)
    }
    if ($normalized -match $forbiddenFilePattern) {
        $violations.Add('Forbidden file: ' + $normalized)
    }

    $fullPath = Join-Path $repositoryRoot $relativePath
    if (!(Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
    $item = Get-Item -LiteralPath $fullPath
    if (!($textExtensions -contains $item.Extension.ToLowerInvariant() -or $item.Name -eq '.gitignore')) { continue }

    $content = [IO.File]::ReadAllText($fullPath)
    foreach ($pattern in $secretPatterns) {
        if ([regex]::IsMatch($content, $pattern)) {
            $violations.Add('Possible secret or private mailbox: ' + $normalized)
            break
        }
    }
}

if ($violations.Count -gt 0) {
    $violations | Sort-Object -Unique | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    Write-Error ('Repository hygiene check failed with {0} finding(s).' -f (($violations | Sort-Object -Unique).Count))
    exit 1
}

Write-Output 'Repository hygiene check passed for all tracked files.'
