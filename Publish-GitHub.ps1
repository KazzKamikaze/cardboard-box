param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$AssetDirectory,
    [string]$Repository='KazzKamikaze/cardboard-box'
)
$ErrorActionPreference='Stop'
if($Version -notmatch '^[A-Za-z0-9._-]+$' -or $Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$'){throw 'Invalid release/repository name.'}
$assets=[IO.Path]::GetFullPath($AssetDirectory)
$required=@('Cardboard Box Patcher.exe','cardboard-assets.zip','cardboard-release.json','Cardboard-Box-Patcher-Source.zip','START-HERE.txt','SHA256SUMS.txt')
foreach($name in $required){if(!(Test-Path -LiteralPath (Join-Path $assets $name) -PathType Leaf)){throw "Missing release asset: $name"}}
$credentialLines=@("protocol=https`nhost=github.com`n`n" | git -c credential.interactive=never credential fill)
$secretLine=$credentialLines | Where-Object {$_ -like 'password=*'} | Select-Object -First 1
if(!$secretLine){throw 'GitHub sign-in is not available in Git Credential Manager.'}
$releaseToken=$secretLine.Substring(9)
$headers=@{Authorization="Bearer $releaseToken";Accept='application/vnd.github+json';'X-GitHub-Api-Version'='2022-11-28';'User-Agent'='Cardboard-Box-Patcher-Release'}
try {
    $repo=$null
    try{$repo=Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$Repository"}catch{if([int]$_.Exception.Response.StatusCode -ne 404){throw}}
    if(!$repo){
        $me=Invoke-RestMethod -Headers $headers -Uri 'https://api.github.com/user'
        if($Repository.Split('/')[0] -ne $me.login){throw 'Can only create the selected account own repository.'}
        $body=@{name=$Repository.Split('/')[1];private=$false;auto_init=$true;description='Cardboard Box modpack patcher and signed release feed. Import the base pack once, then use the patcher for approved updates.'}|ConvertTo-Json
        $repo=Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/json' -Body $body -Uri 'https://api.github.com/user/repos'
    }
    if($repo.private){throw 'The patcher needs an anonymously downloadable release feed; this repository is private.'}
    $tag="v$Version"
    $existing=$null
    try{$existing=Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$Repository/releases/tags/$tag"}catch{if([int]$_.Exception.Response.StatusCode -ne 404){throw}}
    if($existing){throw 'This release tag already exists. Never replace a published update; increment the version and sequence.'}
    $notes=Get-Content -LiteralPath (Join-Path $assets 'START-HERE.txt') -Raw
    $patcherVersion=(Get-Item -LiteralPath (Join-Path $assets 'Cardboard Box Patcher.exe')).VersionInfo.ProductVersion -replace '\+.*$',''
    $body=@{tag_name=$tag;name="Cardboard Box $Version + Patcher $patcherVersion";body=$notes;draft=$true;prerelease=$false}|ConvertTo-Json
    $release=Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/json' -Body $body -Uri "https://api.github.com/repos/$Repository/releases"
    $upload=$release.upload_url -replace '\{\?name,label\}$',''
    $uploadedNames=@{}
    foreach($name in $required){
        if($name -eq 'SHA256SUMS.txt'){
            $uploadedNames.Keys | Sort-Object | ForEach-Object {
                '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $assets $_) -Algorithm SHA256).Hash.ToLowerInvariant(),$uploadedNames[$_]
            } | Set-Content -LiteralPath (Join-Path $assets $name) -Encoding utf8NoBOM
        }
        $path=Join-Path $assets $name
        Write-Output "Uploading $name"
        $asset=Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile $path -Uri "${upload}?name=$([Uri]::EscapeDataString($name))" -TimeoutSec 600
        if($asset.size -ne (Get-Item -LiteralPath $path).Length){throw "Upload size mismatch: $name"}
        $hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if($asset.digest -and $asset.digest -ne "sha256:$hash"){throw "GitHub upload digest mismatch: $name"}
        $uploadedNames[$name]=$asset.name
    }
    $release=Invoke-RestMethod -Method Patch -Headers $headers -ContentType 'application/json' -Body (@{draft=$false;make_latest='true'}|ConvertTo-Json) -Uri "https://api.github.com/repos/$Repository/releases/$($release.id)"
    @{repository=$Repository;url=$release.html_url;tag=$tag;published=$true;assets=$required}|ConvertTo-Json | Set-Content -LiteralPath (Join-Path $assets 'publication.json') -Encoding utf8NoBOM
    Write-Output "Published: $($release.html_url)"
} finally {$releaseToken=$null;$credentialLines=$null;$secretLine=$null;$headers=$null}
