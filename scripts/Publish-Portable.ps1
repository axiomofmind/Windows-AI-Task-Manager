[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0-alpha.2',

    [string]$OutputRoot = 'artifacts'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
$repositoryPrefix = $repositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $artifactRoot.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase) -or $artifactRoot -eq $repositoryRoot) {
    throw 'OutputRoot must resolve to a child of the repository root.'
}

$publishRoot = Join-Path $artifactRoot "AITaskManager-$Version-win-x64"
$zipPath = "$publishRoot.zip"
$checksumPath = "$zipPath.sha256"

if (Test-Path -LiteralPath $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
if (Test-Path -LiteralPath $checksumPath) { Remove-Item -LiteralPath $checksumPath -Force }
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

dotnet publish (Join-Path $repositoryRoot 'src/AITaskManager.Desktop/AITaskManager.Desktop.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishRoot `
    -p:RestoreLockedMode=true `
    -p:Version=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$releaseFiles = @('LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md', 'README.md', 'SECURITY.md', 'config.example.json')
foreach ($file in $releaseFiles) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $file) -Destination $publishRoot
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs') -Destination (Join-Path $publishRoot 'docs') -Recurse

# WebView2 is redistributed in the self-contained desktop output under Microsoft's
# SDK license. Keep the exact NuGet-supplied license and notice beside the app.
[xml]$packageVersions = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Packages.props')
$webViewPackage = $packageVersions.Project.ItemGroup.PackageVersion |
    Where-Object { $_.Include -eq 'Microsoft.Web.WebView2' } |
    Select-Object -First 1
if (-not $webViewPackage.Version) { throw 'Microsoft.Web.WebView2 package version was not found.' }
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$webViewPackageRoot = Join-Path $nugetRoot "microsoft.web.webview2\$($webViewPackage.Version)"
$licenseRoot = Join-Path $publishRoot 'licenses'
New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
foreach ($legalFile in @('LICENSE.txt', 'NOTICE.txt')) {
    $source = Join-Path $webViewPackageRoot $legalFile
    if (-not (Test-Path -LiteralPath $source)) { throw "Required WebView2 legal file is missing: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $licenseRoot "Microsoft.Web.WebView2-$legalFile")
}

& (Join-Path $PSScriptRoot 'New-Sbom.ps1') -RepositoryRoot $repositoryRoot -Version $Version -Destination (Join-Path $publishRoot 'AITaskManager.sbom.cdx.json')

$forbidden = Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object {
    $_.Extension -in @('.db', '.log', '.dmp', '.pfx', '.snk', '.key', '.pem', '.gguf', '.safetensors', '.ckpt', '.pt', '.pth', '.onnx') -or
    $_.Name -in @('config.json', '.env')
}
if ($forbidden) { throw "Forbidden release content detected: $($forbidden.FullName -join ', ')" }

$textFiles = Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object {
    $_.Extension -in @('.json', '.config', '.md', '.txt', '.xml')
}
$developerPatterns = @(
    ('C:' + '\\Users\\' + 'admin'),
    ('E:' + '\\GPU' + 'dashboard'),
    ('D:' + '\\hugging' + 'face'),
    ('\.' + 'openclaw')
) -join '|'
$developerPath = $textFiles | Select-String -Pattern $developerPatterns -SimpleMatch:$false
if ($developerPath) { throw "Developer-machine path detected in release content: $($developerPath.Path | Select-Object -Unique)" }

Compress-Archive -LiteralPath $publishRoot -DestinationPath $zipPath -CompressionLevel Optimal
$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
Set-Content -LiteralPath $checksumPath -Value "$($hash.Hash.ToLowerInvariant()) *$([IO.Path]::GetFileName($zipPath))" -Encoding ascii -NoNewline

[pscustomobject]@{
    Version = $Version
    Directory = $publishRoot
    Archive = $zipPath
    Checksum = $checksumPath
    Sha256 = $hash.Hash.ToLowerInvariant()
}
