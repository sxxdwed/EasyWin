[CmdletBinding()]
param([Parameter(Mandatory)][string]$Root)
$ErrorActionPreference = 'Stop'
$problems = @()
foreach ($project in @('EasyWin.Desktop','EasyWin.WinPE','EasyWin.PostInstall')) {
    $directory = Join-Path $Root "src\$project"
    foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object { $_.Extension -in @('.cs','.xaml') -and $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
        if ($text -match '[\u0400-\u04ff]') { $problems += "$($file.FullName): Cyrillic source text must be moved into resources." }
        if ($text -match '(?:throw\s+new\s+\w+Exception|ThrowIfFailed)\(\s*\$?"[A-Za-z][^"\r\n]*\s[A-Za-z]') {
            $problems += "$($file.FullName): Literal English error text must be moved into resources."
        }
    }
}
if ($problems.Count) { throw ($problems -join [Environment]::NewLine) }
