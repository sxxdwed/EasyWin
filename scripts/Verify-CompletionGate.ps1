[CmdletBinding()]
param(
    [string]$DryRunRoot = (Join-Path $PSScriptRoot '..\artifacts\completion-gate')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$localDotnet = Join-Path $repositoryRoot 'work\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$solution = Join-Path $repositoryRoot 'EasyWin.sln'
$config = Join-Path $repositoryRoot 'config'
$builder = Join-Path $repositoryRoot 'src\EasyWin.Builder\EasyWin.Builder.csproj'
$resolvedDryRun = [System.IO.Path]::GetFullPath($DryRunRoot)

& $dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'RESTORE gate failed.' }

& $dotnet build $solution -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'BUILD gate failed.' }

& $dotnet test $solution -c Release --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'TEST gate failed.' }

& $dotnet run --project $builder -c Release --no-build -- --dry-run --workspace $resolvedDryRun --config $config
if ($LASTEXITCODE -ne 0) { throw 'DRYRUN gate failed.' }

Write-Host 'Completion Gate: PASS (restore, Release build, tests, end-to-end DryRun).'
