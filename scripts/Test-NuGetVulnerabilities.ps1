[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$auditText = dotnet list (Join-Path $repositoryRoot 'AITaskManager.sln') package --vulnerable --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw "NuGet vulnerability audit failed with exit code $LASTEXITCODE." }

$audit = $auditText | ConvertFrom-Json
$findings = [Collections.Generic.List[string]]::new()
foreach ($project in @($audit.projects)) {
    if (-not $project.frameworks) { continue }
    foreach ($framework in @($project.frameworks)) {
        foreach ($groupName in @('topLevelPackages', 'transitivePackages')) {
            $property = $framework.PSObject.Properties[$groupName]
            if (-not $property -or -not $property.Value) { continue }
            foreach ($package in @($property.Value)) {
                if (-not $package.vulnerabilities) { continue }
                foreach ($vulnerability in @($package.vulnerabilities)) {
                    $findings.Add("$($project.path): $($package.id) $($package.resolvedVersion) $($vulnerability.severity) $($vulnerability.advisoryurl)")
                }
            }
        }
    }
}

if ($findings.Count -gt 0) {
    throw "Vulnerable NuGet packages detected:`n$($findings -join "`n")"
}

Write-Host "NuGet vulnerability audit passed for $(@($audit.projects).Count) projects."
