# Focused, local-only bootstrap regression checks. Not part of hosted publication.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'bootstrap.ps1')
function Get-FileHash([string]$LiteralPath,[string]$Algorithm='SHA256') {
  $stream=[IO.File]::OpenRead($LiteralPath); $digest=[Security.Cryptography.HashAlgorithm]::Create($Algorithm)
  try {[pscustomobject]@{Hash=([BitConverter]::ToString($digest.ComputeHash($stream))).Replace('-','')}} finally {$stream.Dispose();$digest.Dispose()}
}
$tools=Assert-Contained (Join-Path $root '.tools/bootstrap-regression') $root
New-Item -ItemType Directory -Force $tools | Out-Null
$tools=Join-Path $tools ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force (Join-Path $tools 'input') | Out-Null
'valid' | Set-Content (Join-Path $tools 'input/tool.exe')
$zip=Join-Path $tools 'tool.zip';$active=Join-Path $tools 'active'
Compress-Archive (Join-Path $tools 'input/*') $zip
Expand-Verified $zip $active tool.exe
if(!(Test-Tree $active $zip)) {throw 'Positive extraction failed.'}
'extra' | Set-Content (Join-Path $active 'extra.txt')
if(Test-Tree $active $zip) {throw 'Extra file accepted.'}
Expand-Verified $zip $active tool.exe
if(!(Test-Tree $active $zip)) {throw 'Reactivation failed.'}
'corrupt' | Set-Content (Join-Path $active 'tool.exe')
if(Test-Tree $active $zip) {throw 'Tampered file accepted.'}
Expand-Verified $zip $active tool.exe
'{truncated' | Set-Content "$active.activation.json"
Expand-Verified $zip $active tool.exe
if(!(Test-Tree $active $zip)) {throw 'Malformed journal lost verified active tree.'}
if(Test-Path "$active.previous") {Move-Item "$active.previous" "$active.retained.$([guid]::NewGuid().ToString('N'))"}
Move-Item $active "$active.previous"
Write-Journal "$active.activation.json" @{active=$active;backup="$active.previous";archiveSha256=(Get-FileHash $zip).Hash;phase='backed-up'}
Expand-Verified $zip $active tool.exe
if(!(Test-Tree $active $zip)) {throw 'Interrupted activation recovery failed.'}
try {$null=Assert-Contained (Join-Path $tools '../outside') $tools;throw 'Escape accepted.'} catch {if($_.Exception.Message -eq 'Escape accepted.') {throw}}
$lock=[IO.File]::Open((Join-Path $tools 'fixture.lock'),'OpenOrCreate','ReadWrite','None')
try {try {$other=[IO.File]::Open((Join-Path $tools 'fixture.lock'),'OpenOrCreate','ReadWrite','None');$other.Dispose();throw 'Concurrent lock accepted.'} catch {if($_.Exception.Message -eq 'Concurrent lock accepted.') {throw}}} finally {$lock.Dispose()}
Write-Output 'PASS: extraction, extra and tampered files, reactivation, malformed journal, interrupted activation, path containment, exclusive lock'
