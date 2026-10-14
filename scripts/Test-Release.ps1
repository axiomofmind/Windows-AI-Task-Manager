[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $repositoryRoot 'AITaskManager.sln'
$webRoot = Join-Path $repositoryRoot 'src\AITaskManager.Web'

function Assert-LastExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "$step failed with exit code $LASTEXITCODE." }
}

dotnet restore $solution --locked-mode
Assert-LastExitCode 'Locked .NET restore'
dotnet build $solution --configuration Release --no-restore
Assert-LastExitCode '.NET build'
dotnet format $solution --no-restore --verify-no-changes --verbosity minimal
Assert-LastExitCode '.NET format verification'
dotnet test $solution --configuration Release --no-build --logger 'console;verbosity=minimal'
Assert-LastExitCode '.NET tests'

Push-Location $webRoot
try {
    npm ci
    Assert-LastExitCode 'Web dependency restore'
    npm run lint
    Assert-LastExitCode 'Web lint'
    npm test
    Assert-LastExitCode 'Web type check'
    npm run build
    Assert-LastExitCode 'Web production build'
    npm audit --audit-level=high
    Assert-LastExitCode 'npm vulnerability audit'
}
finally {
    Pop-Location
}

& (Join-Path $PSScriptRoot 'Test-NuGetVulnerabilities.ps1')
& (Join-Path $PSScriptRoot 'Test-RepositoryHygiene.ps1')
Write-Host 'All local release gates passed.'
