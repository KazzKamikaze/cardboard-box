param(
    [Parameter(Mandatory)][string]$Mrpack,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][long]$Sequence,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PrivateKeyPath=(Join-Path $env:USERPROFILE '.cardboard-box\keys\release-private.pem')
)
$ErrorActionPreference='Stop'
if($Version -notmatch '^[A-Za-z0-9._-]+$'){throw 'Use a simple immutable release version.'}
$project=Join-Path $PSScriptRoot 'CardboardPatcher.csproj'
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath (Join-Path $output 'cardboard-release.json')){throw 'Release exists; choose a new version and sequence.'}
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $output --nologo -v minimal
if($LASTEXITCODE -ne 0){throw 'Patcher build failed.'}
# dotnet waits for the console-hosted publisher; invoking a WinExe directly in PowerShell may return early.
$dll=Join-Path $PSScriptRoot 'bin\Release\net9.0-windows\win-x64\Cardboard Box Patcher.dll'
$base="https://github.com/KazzKamikaze/cardboard-box/releases/download/v$Version"
$env:CARDBOARD_ATL_LICENSE=Join-Path $PSScriptRoot 'third-party\AllTheLeaks-LICENSE.txt'
dotnet $dll --build-release ([IO.Path]::GetFullPath($Mrpack)) $output ([IO.Path]::GetFullPath($PrivateKeyPath)) $base $Sequence
if($LASTEXITCODE -ne 0){throw 'Release manifest generation failed. See the patcher error log in TEMP.'}
Get-ChildItem -LiteralPath $output -File | Where-Object Name -NotMatch '^SHA256SUMS' | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),$_.Name
} | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8NoBOM
