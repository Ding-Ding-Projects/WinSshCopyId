param([Parameter(Mandatory=$true)][ValidateSet('build','installer','dependencies')][string]$Action,[Parameter(ValueFromRemainingArguments=$true)][string[]]$Options)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$ProgressPreference='SilentlyContinue'
function Get-FileHash([string]$LiteralPath,[string]$Algorithm='SHA256') {
  $stream=[IO.File]::OpenRead($LiteralPath)
  $digest=[Security.Cryptography.HashAlgorithm]::Create($Algorithm)
  try {return [pscustomobject]@{Hash=([BitConverter]::ToString($digest.ComputeHash($stream))).Replace('-','');Path=$LiteralPath}} finally {$stream.Dispose();$digest.Dispose()}
}
$root=Split-Path $PSScriptRoot -Parent
Set-Location $root
$m=Get-Content (Join-Path $root 'build/manifest.json') -Raw | ConvertFrom-Json
$silent=$env:SILENT -eq '1'
$run=$env:RUN_AFTER_BUILD -eq '1'
foreach($option in $Options) {
  switch($option) { '/s' {$silent=$true} '--silent' {$silent=$true} '/run' {$run=$true} '--run' {$run=$true} default {throw "Unknown option: $option"} }
}
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
function Invoke-Checked([string]$File,[string[]]$Arguments) {
  & $File @Arguments
  if($LASTEXITCODE -ne 0) {throw "Command failed with exit ${LASTEXITCODE}: $File"}
}
function Get-Source {
  $sha=(& git rev-parse HEAD).Trim(); if($LASTEXITCODE -ne 0) {throw 'Cannot resolve source commit.'}
  $dirty=@(& git status --porcelain --untracked-files=all)
  if($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) {throw 'Build requires a committed, unchanged source tree and index.'}
  return $sha
}
function Get-VerifiedDownload($Spec,[string]$Destination,[string]$Algorithm) {
  $expected=$Spec.($Algorithm.ToLowerInvariant())
  if((Test-Path $Destination) -and (Get-FileHash $Destination -Algorithm $Algorithm).Hash -eq $expected) {return}
  $temp="$Destination.$PID.$([guid]::NewGuid().ToString('N')).download"
  try {
    Write-Host "Downloading pinned dependency from $($Spec.url)"
    if(Get-Command curl.exe -ErrorAction SilentlyContinue) {
      Invoke-Checked 'curl.exe' @('--fail','--location','--silent','--show-error','--connect-timeout','30','--max-time','900','--output',$temp,$Spec.url)
    } else {Invoke-WebRequest -UseBasicParsing -TimeoutSec 900 -Uri $Spec.url -OutFile $temp}
    if((Get-FileHash $temp -Algorithm $Algorithm).Hash -ne $expected) {throw "Download digest mismatch: $($Spec.url)"}
    Move-Item -LiteralPath $temp -Destination $Destination -Force
  } finally {if(Test-Path $temp) {Remove-Item -LiteralPath $temp -Force}}
}
. (Join-Path $PSScriptRoot 'bootstrap.ps1')
try {
  $timer=[Diagnostics.Stopwatch]::StartNew()
  if($env:OS -ne 'Windows_NT' -or ![Environment]::Is64BitOperatingSystem) {throw 'This build requires 64-bit Windows.'}
  Write-Host 'Using project-local portable tooling; administrator rights are not required.'
  $tools=Assert-Contained (Join-Path $root '.tools') $root; New-Item -ItemType Directory -Force $tools | Out-Null
  $lock=[IO.File]::Open((Join-Path $tools 'bootstrap.lock'),'OpenOrCreate','ReadWrite','None')
  try {
    Get-VerifiedDownload $m.sdk (Join-Path $tools 'sdk.zip') SHA512
    Expand-Verified (Join-Path $tools 'sdk.zip') (Join-Path $tools 'dotnet') 'dotnet.exe'
    Get-VerifiedDownload $m.nuget (Join-Path $tools 'nuget.exe') SHA256
    Get-VerifiedDownload $m.squirrel (Join-Path $tools 'squirrel.zip') SHA256
    Expand-Verified (Join-Path $tools 'squirrel.zip') (Join-Path $tools 'squirrel') 'tools/Squirrel.exe'
  } finally {$lock.Dispose()}
  $dotnet=Join-Path $tools 'dotnet/dotnet.exe'
  $env:DOTNET_ROOT=Split-Path $dotnet; $env:DOTNET_MULTILEVEL_LOOKUP='0'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
  $env:PATH="$env:DOTNET_ROOT;$env:PATH"
  $sdk=(& $dotnet --version).Trim(); if($sdk -ne $m.sdk.version) {throw "Expected SDK $($m.sdk.version), found $sdk"}
  Write-Host "Verified portable SDK $sdk and packaging tools. Elapsed: $($timer.Elapsed)"
  if($Action -eq 'dependencies') {exit 0}
  $buildLock=[IO.File]::Open((Join-Path $tools 'producer.lock'),'OpenOrCreate','ReadWrite','None')
  $source=Get-Source
  $manifestHash=(Get-FileHash 'build/manifest.json' -Algorithm SHA256).Hash
  $publish=Resolve-Output $m.publishDirectory @('artifacts/publish')
  if(Test-Path $publish) {Remove-Item -LiteralPath $publish -Recurse -Force}
  New-Item -ItemType Directory -Force $publish | Out-Null
  $version=$m.version
  if($env:BUILD_VERSION) {$version=$env:BUILD_VERSION}
  if($version -notmatch '^\d+\.\d+\.\d+$') {throw 'BUILD_VERSION must have three numeric components.'}
  Invoke-Checked $dotnet @('publish',$m.project,'-c','Release','-r',$m.runtime,'--self-contained','true','-p:Platform=x64','-p:WindowsAppSDKSelfContained=true','-p:WindowsPackageType=None',"-p:Version=$version",'-o',$publish)
  foreach($name in $m.requiredPayload) { $path=Join-Path $publish $name; if(!(Test-Path $path) -or (Get-Item $path).Length -eq 0) {throw "Self-contained payload missing: $name"} }
  Copy-Item (Join-Path $tools 'squirrel/tools/Squirrel.exe') (Join-Path $publish 'Update.exe')
  @{updateFeed=$m.updateFeed;version=$version} | ConvertTo-Json | Set-Content (Join-Path $publish 'update-config.json')
  if((Get-Source) -ne $source) {throw 'Source changed during build.'}
  $payload=@(Get-ChildItem $publish -Recurse -File | ForEach-Object { @{path=$_.FullName.Substring($publish.Length+1);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash;bytes=$_.Length} })
  if((Get-FileHash 'build/manifest.json' -Algorithm SHA256).Hash -ne $manifestHash) {throw 'Manifest changed during build.'}
  $receipt=@{schemaVersion=1;sourceTree=(& git rev-parse 'HEAD^{tree}').Trim();indexTree=(& git write-tree).Trim();sourceCommit=$source;manifestSha256=$manifestHash;version=$version;runtime=$m.runtime;command='build.bat /s';files=$payload;createdUtc=[DateTime]::UtcNow.ToString('o')}
  $receipt | ConvertTo-Json -Depth 6 | Set-Content 'artifacts/build-receipt.json'
  Write-Host "Built $($m.executable) from $source. Elapsed: $($timer.Elapsed)"
  if($Action -eq 'installer') {
    $releases=Resolve-Output $m.releaseDirectory @('artifacts/releases')
    if(Test-Path $releases) {Remove-Item -LiteralPath $releases -Recurse -Force}
    New-Item -ItemType Directory -Force $releases | Out-Null
    $packages=Resolve-Output 'artifacts/packages' @('artifacts/packages'); New-Item -ItemType Directory -Force $packages | Out-Null
    Invoke-Checked (Join-Path $tools 'nuget.exe') @('pack','packaging/WinSshCopyId.nuspec','-Version',$version,'-Properties',"payload=$publish",'-OutputDirectory',$packages,'-NoDefaultExcludes','-NonInteractive')
    Invoke-Checked (Join-Path $tools 'squirrel/tools/Squirrel.com') @('--releasify',(Join-Path $packages "WinSshCopyId.$version.nupkg"),'--releaseDir',$releases,'--no-msi','--no-delta')
    foreach($name in @('Setup.exe','RELEASES',"WinSshCopyId-$version-full.nupkg")) {
      $path=Join-Path $releases $name
      if(!(Test-Path $path) -or (Get-Item $path).Length -eq 0) {throw "Missing installer output: $name"}
    }
    $setup=Join-Path $releases 'Setup.exe'
    if((Get-AuthenticodeSignature $setup).Status -ne 'NotSigned') {throw 'Expected an unsigned Squirrel setup executable.'}
    if((Get-Source) -ne $source -or (Get-FileHash 'build/manifest.json' -Algorithm SHA256).Hash -ne $manifestHash) {throw 'Source or manifest changed during packaging.'}
    $assets=@(Get-ChildItem $releases -File | ForEach-Object { @{name=$_.Name;bytes=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash} })
    @{schemaVersion=1;sourceCommit=$source;version=$version;manifestSha256=$manifestHash;unsigned=$true;assets=$assets} | ConvertTo-Json -Depth 6 | Set-Content 'artifacts/installer-receipt.json'
    $assets | ForEach-Object {Write-Host "$($_.name) $($_.bytes) bytes SHA256 $($_.sha256)"}
    Write-Host 'Genuine unsigned Squirrel.Windows installer produced. Installation and runtime verification are separate.'
  }
  if(!$silent -and !$run -and $Action -eq 'build') {$run=(Read-Host 'Run the built application? [y/N]') -eq 'y'}
  if($run -and $Action -eq 'build') {if((Get-Source) -ne $source) {throw 'Source changed before launch.'}; Start-Process -FilePath (Join-Path $publish $m.executable)}
  exit 0
} catch {Write-Error $_; exit 1} finally {if(Get-Variable buildLock -ErrorAction SilentlyContinue) {$buildLock.Dispose()}}



