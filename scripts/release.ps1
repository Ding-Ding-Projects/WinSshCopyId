param([Parameter(Mandatory=$true)][string]$SourceCommit,[Parameter(Mandatory=$true)][string]$Repository,[Parameter(Mandatory=$true)][string]$RunId,[Parameter(Mandatory=$true)][string]$RunAttempt,[bool]$Stable=$false)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Set-Location (Split-Path $PSScriptRoot -Parent)
function Get-FileHash([string]$LiteralPath,[string]$Algorithm='SHA256') {
  $stream=[IO.File]::OpenRead($LiteralPath);$digest=[Security.Cryptography.HashAlgorithm]::Create($Algorithm)
  try {[pscustomobject]@{Hash=([BitConverter]::ToString($digest.ComputeHash($stream))).Replace('-','')}} finally {$stream.Dispose();$digest.Dispose()}
}
function Invoke-Gh([string[]]$Arguments) { $result=& gh @Arguments; if($LASTEXITCODE -ne 0) {throw "GitHub CLI failed: $($Arguments[0])"}; return $result }
if($SourceCommit -notmatch '^[a-f0-9]{40}$' -or $RunId -notmatch '^\d+$' -or $RunAttempt -notmatch '^\d+$') {throw 'Invalid exact source or run identity.'}
if((& git rev-parse HEAD).Trim() -ne $SourceCommit -or @(& git status --porcelain).Count -ne 0) {throw 'Publisher requires unchanged exact source.'}
& git fetch origin main --no-tags
if($LASTEXITCODE -ne 0) {throw 'Cannot verify main lineage.'}
$mainBase=(& git merge-base $SourceCommit origin/main).Trim()
if($LASTEXITCODE -ne 0 -or !$mainBase) {throw 'Cannot prove candidate main lineage.'}
& git merge-base --is-ancestor $mainBase $SourceCommit
if($LASTEXITCODE -ne 0) {throw 'Release source must descend from main lineage.'}
$receipt=Get-Content artifacts/installer-receipt.json -Raw | ConvertFrom-Json
if($receipt.sourceCommit -ne $SourceCommit -or !$receipt.unsigned -or $receipt.manifestSha256 -ne (Get-FileHash -LiteralPath 'build/manifest.json' -Algorithm SHA256).Hash) {throw 'Installer receipt source or signing mismatch.'}
$assets=@()
foreach($asset in $receipt.assets) {
  if($asset.name -notmatch '^(Setup\.exe|RELEASES|WinSshCopyId-[0-9.]+-full\.nupkg)$') {throw 'Unexpected release asset name.'}
  $path=Join-Path 'artifacts/releases' $asset.name
  if((Get-Item $path).Length -ne $asset.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $asset.sha256) {throw "Release asset mismatch: $($asset.name)"}
  $assets+=$path
}
if($assets.Count -ne 3) {throw 'Expected exactly three Squirrel release assets.'}
$tag="v$($receipt.version)-run.$RunId.$RunAttempt"
$existing=& gh release view $tag --repo $Repository --json tagName 2>$null
if($LASTEXITCODE -eq 0) {throw 'Immutable release tag already exists; no overwrite is permitted.'}
$body="Native Windows x64 build from commit $SourceCommit.`n`nUnsigned Squirrel.Windows installer. Windows may show an unknown publisher warning.`n`nBuild and packaging completed. Installation, GUI behavior, update, rollback, and fresh-environment acceptance remain separate verification obligations."
$notes=Join-Path 'artifacts' 'release-notes.md'; [IO.File]::WriteAllText((Join-Path (Get-Location) $notes),$body)
$latest=if($Stable) {'--latest=true'} else {'--latest=false'}
if($Stable -and ((& git rev-parse origin/main).Trim() -ne $SourceCommit)) {$latest='--latest=false';Write-Host 'A newer main head exists; publishing this exact source without changing latest.'}
Invoke-Gh (@('release','create',$tag,$latest,'--repo',$Repository,'--target',$SourceCommit,'--title',"WinSshCopyId $($receipt.version)",'--notes-file',$notes)+$assets) | Out-Host
$release=(Invoke-Gh @('release','view',$tag,'--repo',$Repository,'--json','isDraft,isPrerelease,tagName,assets,url')) | ConvertFrom-Json
if($release.isDraft -or $release.isPrerelease -or $release.tagName -ne $tag) {throw 'Published release is not the expected normal release.'}
$ref=(Invoke-Gh @('api',"repos/$Repository/git/ref/tags/$tag")) | ConvertFrom-Json
$object=$ref.object; $seen=@{}; $depth=0
while($object.type -eq 'tag') {
  if($depth++ -ge 8 -or $seen.ContainsKey($object.sha)) {throw 'Invalid annotated tag chain.'}
  $seen[$object.sha]=$true
  $object=((Invoke-Gh @('api',"repos/$Repository/git/tags/$($object.sha)")) | ConvertFrom-Json).object
}
if($object.type -ne 'commit' -or $object.sha -ne $SourceCommit) {throw 'Published tag resolves to another source.'}
foreach($asset in $receipt.assets) {
  $remote=@($release.assets | Where-Object name -eq $asset.name)
  if($remote.Count -ne 1 -or $remote[0].size -ne $asset.bytes) {throw "Published asset missing or wrong size: $($asset.name)"}
}
$verify=Join-Path 'artifacts' "release-readback-$RunId-$RunAttempt"; New-Item -ItemType Directory $verify | Out-Null
Invoke-Gh @('release','download',$tag,'--repo',$Repository,'--dir',$verify) | Out-Host
foreach($asset in $receipt.assets) {if((Get-FileHash -LiteralPath (Join-Path $verify $asset.name) -Algorithm SHA256).Hash -ne $asset.sha256) {throw 'Published download hash mismatch.'}}
@{tag=$tag;sourceCommit=$SourceCommit;url=$release.url;isDraft=$release.isDraft;isPrerelease=$release.isPrerelease;verifiedAssets=$receipt.assets} | ConvertTo-Json -Depth 6 | Set-Content artifacts/release-receipt.json
Write-Host "Verified normal release: $($release.url)"




