[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$Dotnet,
    [string]$CertificateThumbprint,
    [string]$SignTool,
    [string]$TimestampUrl = 'https://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $output) { throw 'Output must be a new directory.' }
if (git -C $repository status --porcelain) { throw 'Commit all source changes before a tagged release.' }
$commit = git -C $repository rev-parse --verify "refs/tags/$Tag^{commit}"
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'An existing committed tag is required.' }
$dotnetPath = (Resolve-Path -LiteralPath $Dotnet).Path
New-Item -ItemType Directory -Path $output | Out-Null
$checkout = Join-Path $output 'source'
git -C $repository worktree add --detach $checkout $commit
if ($LASTEXITCODE -ne 0) { throw 'Clean worktree creation failed.' }
if (git -C $checkout status --porcelain) { throw 'Release checkout is dirty.' }
$env:PATH = (Split-Path -Parent $dotnetPath) + [IO.Path]::PathSeparator + $env:PATH
& (Join-Path $checkout 'scripts\Build-Release.ps1') -OutputRoot (Join-Path $output 'bundle')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& $dotnetPath run --project (Join-Path $checkout 'src\EasyWin.Builder') -c Release -- --dry-run --workspace (Join-Path $output 'validation') --config (Join-Path $checkout 'config')
if ($LASTEXITCODE -ne 0) { throw 'DryRun failed.' }
$bundle = Join-Path $output 'bundle\EasyWin'
$binary = Join-Path $bundle 'EasyWin.Desktop.exe'
$version = (Get-Item -LiteralPath $binary).VersionInfo.ProductVersion
if (-not $version.EndsWith("+$commit")) { throw 'Binary source SHA does not match the tag.' }
$signing = 'Unsigned development build'
if ($CertificateThumbprint) {
    if (-not $SignTool -or -not (Test-Path -LiteralPath $SignTool)) { throw 'A real SignTool path is required.' }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
    if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'A current code-signing certificate with a private key is required.' }
    if ($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Where-Object { $_.Format($false) -match '1.3.6.1.5.5.7.3.3|Code Signing' }) {
        foreach ($exe in Get-ChildItem -LiteralPath $bundle -Filter 'EasyWin*.exe' -Recurse -File) {
            & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $exe.FullName
            if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing failed.' }
            & $SignTool verify /pa $exe.FullName
            if ($LASTEXITCODE -ne 0) { throw 'Authenticode verification failed.' }
        }
        $signing = 'Authenticode signed'
    } else { throw 'Certificate does not have Code Signing EKU.' }
}
$provenance = [ordered]@{ version=$version; commit=$commit; tag=$Tag; buildTimestampUtc=[DateTime]::UtcNow.ToString('O'); repository='https://github.com/sxxdwed/EasyWin'; signing=$signing; vmE2E='NOT RUN' }
Set-Content -LiteralPath (Join-Path $bundle 'BUILD-STATUS.txt') -Value "$signing. Real VM E2E: NOT RUN." -Encoding utf8
$provenance | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bundle 'build-provenance.json') -Encoding utf8
if (git -C $checkout status --porcelain) { throw 'Build unexpectedly modified tracked source.' }
$zip = Join-Path $output 'EasyWin-win-x64.zip'
Compress-Archive -LiteralPath $bundle -DestinationPath $zip
((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash + '  EasyWin-win-x64.zip') | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output $zip
