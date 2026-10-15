# Runs in the root build script's scope and shares its verified download primitives.
$siteScript=Join-Path $root 'scripts/build-site.mjs'
if(!(Test-Path -LiteralPath $siteScript -PathType Leaf)) {throw 'Website build helper scripts/build-site.mjs is absent; integrate the documentation source before building the website.'}
$source=Get-Source
$node=$null
$installed=Get-Command node.exe -ErrorAction SilentlyContinue
if($installed) {
  try {$candidateOutput=& $installed.Source --version 2>$null;$probeExit=$LASTEXITCODE;$candidateVersion=($candidateOutput -join "`n").Trim()}
  catch {$probeExit=1;$candidateVersion=''}
  if($probeExit -eq 0 -and $candidateVersion -match '^v(\d+)\.' -and [int]$Matches[1] -ge $m.node.minimumMajor) {$node=$installed.Source}
}
if(!$node) {
  Write-Host "Acquiring pinned portable Node.js $($m.node.version); no global installation or PATH change."
  $tools=Assert-Contained (Join-Path $root '.tools') $root
  New-Item -ItemType Directory -Force $tools | Out-Null
  $lock=[IO.File]::Open((Join-Path $tools 'bootstrap.lock'),'OpenOrCreate','ReadWrite','None')
  try {
    Get-VerifiedDownload $m.node (Join-Path $tools 'node.zip') SHA256
    Expand-Verified (Join-Path $tools 'node.zip') (Join-Path $tools 'node') "$($m.node.directory)/node.exe"
  } finally {$lock.Dispose()}
  $node=Assert-Contained (Join-Path $tools "node/$($m.node.directory)/node.exe") $tools
}
$nodeVersion=(& $node --version).Trim()
if($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v(\d+)\.' -or [int]$Matches[1] -lt $m.node.minimumMajor) {throw 'Node.js 22 or newer could not be verified.'}
Write-Host "Website build using verified Node.js $nodeVersion."
Invoke-Checked $node @($siteScript)
if((Get-Source) -ne $source) {throw 'Source changed during website build.'}
New-Item -ItemType Directory -Force (Join-Path $root 'artifacts') | Out-Null
@{sourceCommit=$source;nodeVersion=$nodeVersion;manifestSha256=(Get-FileHash 'build/manifest.json' -Algorithm SHA256).Hash;builtAtUtc=[DateTime]::UtcNow.ToString('o');command='build.bat --site'} | ConvertTo-Json | Set-Content (Join-Path $root 'artifacts/site-build-receipt.json')

