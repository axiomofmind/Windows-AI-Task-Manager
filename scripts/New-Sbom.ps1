[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$RepositoryRoot,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Destination
)

$ErrorActionPreference = 'Stop'
$components = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)

$dotnetJson = dotnet list (Join-Path $RepositoryRoot 'AITaskManager.sln') package --include-transitive --format json --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate .NET dependencies for the SBOM.' }
$dotnetInventory = $dotnetJson | ConvertFrom-Json
foreach ($project in $dotnetInventory.projects) {
    foreach ($framework in $project.frameworks) {
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            if (-not $package.id -or -not $package.resolvedVersion) { continue }
            $key = "nuget:$($package.id):$($package.resolvedVersion)"
            $components[$key] = [ordered]@{
                type = 'library'
                name = $package.id
                version = $package.resolvedVersion
                purl = "pkg:nuget/$([Uri]::EscapeDataString($package.id))@$([Uri]::EscapeDataString($package.resolvedVersion))"
            }
        }
    }
}

$lock = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src/AITaskManager.Web/package-lock.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($entry in $lock.packages.GetEnumerator()) {
    if (-not $entry.Key -or -not $entry.Value.version) { continue }
    $name = $entry.Value.name
    if (-not $name) { $name = $entry.Key -replace '^.*node_modules/', '' }
    $key = "npm:${name}:$($entry.Value.version)"
    $component = [ordered]@{
        type = 'library'
        name = $name
        version = $entry.Value.version
        purl = "pkg:npm/$([Uri]::EscapeDataString($name))@$([Uri]::EscapeDataString($entry.Value.version))"
    }
    if ($entry.Value.license) { $component.licenses = @(@{ license = @{ id = $entry.Value.license } }) }
    $components[$key] = $component
}

$sbom = [ordered]@{
    '$schema' = 'http://cyclonedx.org/schema/bom-1.5.schema.json'
    bomFormat = 'CycloneDX'
    specVersion = '1.5'
    serialNumber = "urn:uuid:$([Guid]::NewGuid())"
    version = 1
    metadata = [ordered]@{
        timestamp = [DateTimeOffset]::UtcNow.ToString('O')
        component = [ordered]@{ type = 'application'; name = 'AI Task Manager'; version = $Version }
    }
    components = @($components.Values | Sort-Object name, version)
}

$directory = Split-Path -Parent $Destination
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$sbom | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Destination -Encoding utf8NoBOM
