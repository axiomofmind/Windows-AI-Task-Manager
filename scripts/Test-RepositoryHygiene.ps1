[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tracked = @(git -C $repositoryRoot ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate publishable repository files.' }

$forbiddenNames = @(
    '\.env(?:\.|$)', '\.(?:pfx|snk|key|pem|db|db-wal|db-shm|log|dmp)$',
    '\.(?:gguf|safetensors|ckpt|pt|pth|onnx)$',
    '(^|/)(?:data|logs|diagnostics|models|datasets|checkpoints)/'
)
$badNames = foreach ($file in $tracked) {
    if ($forbiddenNames | Where-Object { $file -match $_ }) { $file }
}
if ($badNames) { throw "Forbidden tracked files: $($badNames -join ', ')" }

$textExtensions = @('.cs', '.csproj', '.props', '.json', '.md', '.ps1', '.ts', '.tsx', '.css', '.yml', '.yaml', '.xml')
$patterns = @(
    ('C:' + '\\Users\\' + 'admin'),
    ('E:' + '\\GPU' + 'dashboard'),
    ('D:' + '\\hugging' + 'face'),
    ('\.' + 'openclaw'),
    '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '(?i)(?:api[_-]?key|secret|token|password)\s*[:=]\s*["''][A-Za-z0-9_\-]{20,}'
)
$findings = [Collections.Generic.List[string]]::new()
foreach ($relative in $tracked) {
    $path = Join-Path $repositoryRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    if ([IO.Path]::GetExtension($path) -notin $textExtensions) { continue }
    foreach ($pattern in $patterns) {
        $matches = Select-String -LiteralPath $path -Pattern $pattern
        foreach ($match in $matches) { $findings.Add("$relative`:$($match.LineNumber)") }
    }
}
if ($findings.Count -gt 0) { throw "Repository hygiene findings: $($findings -join ', ')" }

Write-Host "Repository hygiene check passed for $($tracked.Count) publishable files."
