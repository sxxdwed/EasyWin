[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\release'),
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputRoot)
$localDotnet = Join-Path $repositoryRoot 'work\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$solution = Join-Path $repositoryRoot 'EasyWin.sln'
$bundle = Join-Path $resolvedOutput 'EasyWin'

New-Item -ItemType Directory -Path $bundle -Force | Out-Null

& $dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

& $dotnet build $solution -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

if (-not $SkipTests) {
    & $dotnet test $solution -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

& $dotnet publish (Join-Path $repositoryRoot 'src\EasyWin.Desktop\EasyWin.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $bundle
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }

& $dotnet publish (Join-Path $repositoryRoot 'src\EasyWin.WinPE\EasyWin.WinPE.csproj') -c Release -p:PublishProfile=WinPE -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $bundle 'EasyWin.WinPE')
if ($LASTEXITCODE -ne 0) { throw 'WinPE publish failed.' }

& $dotnet publish (Join-Path $repositoryRoot 'src\EasyWin.PostInstall\EasyWin.PostInstall.csproj') -c Release -p:PublishProfile=PostInstall -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $bundle 'EasyWin.PostInstall')
if ($LASTEXITCODE -ne 0) { throw 'PostInstall publish failed.' }

# Public release bundles intentionally exclude symbols because portable PDBs can
# disclose local source paths. Symbols remain available in local build outputs.
Get-ChildItem -LiteralPath $bundle -Filter '*.pdb' -File -Recurse | Remove-Item -Force

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $bundle -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $bundle -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'SECURITY.md') -Destination $bundle -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'DEPLOYMENT-READY.md') -Destination $bundle -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs') -Destination $bundle -Recurse -Force

Write-Host "EasyWin release bundle: $bundle"
