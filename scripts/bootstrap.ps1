function Assert-Contained([string]$Path,[string]$Parent) {
  $full=[IO.Path]::GetFullPath($Path); $base=[IO.Path]::GetFullPath($Parent).TrimEnd('\')+'\'
  if(!$full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) {throw "Path is outside its owned root: $full"}
  $walk=$full
  while($walk.Length -ge $base.TrimEnd('\').Length) {
    if(Test-Path -LiteralPath $walk) {if((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {throw "Reparse path is not allowed: $walk"}}
    $next=Split-Path $walk -Parent; if(!$next -or $next -eq $walk) {break}; $walk=$next
  }
  return $full
}
function Resolve-Output([string]$Relative,[string[]]$Allowed) {
  if($Relative -notin $Allowed -or [IO.Path]::IsPathRooted($Relative)) {throw "Unexpected output path: $Relative"}
  return Assert-Contained (Join-Path $root $Relative) $root
}
$script:zipInventories=@{}
function Get-ArchiveInventory([string]$Zip) {
  $digest=(Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash
  if($script:zipInventories.ContainsKey($digest)) {return $script:zipInventories[$digest]}
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $archive=[IO.Compression.ZipFile]::OpenRead($Zip)
  try {
    $entries=@()
    foreach($entry in $archive.Entries) {
      $relative=$entry.FullName.Replace('/','\')
      if([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|\\)\.\.(\\|$)|:') {throw 'Archive entry escapes extraction root.'}
      if(!$entry.Name) {continue}
      $stream=$entry.Open(); $sha=[Security.Cryptography.SHA256]::Create()
      try {$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')} finally {$stream.Dispose();$sha.Dispose()}
      $entries+=@{path=$relative;sha256=$hash;bytes=$entry.Length}
    }
    if($entries.Count -eq 0) {throw 'Archive is empty.'}
    $script:zipInventories[$digest]=$entries
    return $entries
  } finally {$archive.Dispose()}
}
function Test-Tree([string]$Directory,[string]$Zip,[switch]$Diagnose,[string]$OwnerRoot=$tools) {
  if(!(Test-Path -LiteralPath $Directory -PathType Container)) {if($Diagnose){Write-Host 'Inventory mismatch: extraction directory is missing.'};return $false}
  $phase='containment';$logicalMember='none'
  try {
    $null=Assert-Contained $Directory $OwnerRoot
    $phase='archive-inventory';$expected=@(Get-ArchiveInventory $Zip)
    $phase='disk-inventory';$actual=@(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force)
    $valid=$true
    $members=@{}
    foreach($item in $actual) {$members[$item.FullName.Substring($Directory.TrimEnd('\').Length+1).Replace('/','\')]=$item}
    $expectedNames=@{}
    foreach($entry in $expected) {
      $phase='member-path';$logicalMember=if($entry -is [Collections.IDictionary] -and $entry.Contains('path')) {$entry['path']} else {'unknown'}
      $path=Assert-Contained (Join-Path $Directory $entry.path) $Directory
      $relative=$path.Substring($Directory.TrimEnd('\').Length+1).Replace('/','\')
      if($expectedNames.ContainsKey($relative)) {throw 'Archive contains duplicate logical member paths.'}
      $expectedNames[$relative]=$true
      if(!$members.ContainsKey($relative)) {$valid=$false;if($Diagnose){Write-Host "Inventory missing member: $relative"};continue}
      $phase='member-metadata';$item=Get-Item -LiteralPath $path -Force
      if($item.Length -ne $entry.bytes) {$valid=$false;if($Diagnose){Write-Host "Inventory length mismatch: $relative expected=$($entry.bytes) actual=$($item.Length)"};continue}
      $phase='member-hash'
      if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) {$valid=$false;if($Diagnose){Write-Host "Inventory hash mismatch: $relative"}}
    }
    foreach($relative in $members.Keys) {if(!$expectedNames.ContainsKey($relative)) {$valid=$false;if($Diagnose){Write-Host "Inventory extra member: $relative"}}}
    if($actual.Count -ne $expected.Count) {$valid=$false;if($Diagnose){Write-Host "Inventory member count: expected=$($expected.Count) actual=$($actual.Count)"}}
    return $valid
  } catch {
    if($Diagnose) {
      $property='unknown'
      if($_.Exception.Message -match "property '([A-Za-z0-9_.]{1,80})'") {$property=$Matches[1]}
      $line=$_.InvocationInfo.ScriptLineNumber
      Write-Host "Inventory inspection failed: $($_.Exception.GetType().Name) phase=$phase property=$property line=$line member=$logicalMember"
    }
    return $false
  }
}
function Write-Journal([string]$Path,$State) {
  $temp=Assert-Contained "$Path.$PID.tmp" $tools
  [IO.File]::WriteAllText($temp,($State | ConvertTo-Json -Depth 4))
  Move-Item -LiteralPath $temp -Destination $Path -Force
}
function Expand-Verified([string]$Zip,[string]$Directory,[string]$Required) {
  $Directory=Assert-Contained $Directory $tools
  $backup=Assert-Contained "$Directory.previous" $tools
  $journal=Assert-Contained "$Directory.activation.json" $tools
  if(Test-Path -LiteralPath $journal) {
    $validJournal=$false
    try {
      $state=Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
      $validJournal=($state.active -eq $Directory -and $state.backup -eq $backup -and $state.archiveSha256 -eq (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash -and $state.phase -in @('staged','backed-up','activated'))
    } catch {}
    if(!(Test-Tree $Directory $Zip)) {
      if(Test-Tree $backup $Zip) {
        if(Test-Path -LiteralPath $Directory) {Move-Item -LiteralPath $Directory -Destination (Assert-Contained "$Directory.invalid.$([guid]::NewGuid().ToString('N'))" $tools)}
        Move-Item -LiteralPath $backup -Destination $Directory
      } elseif(!$validJournal) {throw 'Invalid activation journal and no verified active or backup tree; retained for recovery.'}
    }
    Move-Item -LiteralPath $journal -Destination (Assert-Contained "$journal.recovered.$([guid]::NewGuid().ToString('N'))" $tools)
  }
  if(Test-Tree $Directory $Zip) {return}
  $stage=Assert-Contained "$Directory.stage.$PID.$([guid]::NewGuid().ToString('N'))" $tools
  $null=Get-ArchiveInventory $Zip
  Expand-Archive -LiteralPath $Zip -DestinationPath $stage
  if(!(Test-Path (Join-Path $stage $Required)) -or !(Test-Tree $stage $Zip -Diagnose)) {throw 'Extracted dependency verification failed.'}
  $state=@{active=$Directory;backup=$backup;stage=$stage;archiveSha256=(Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash;phase='staged'}
  Write-Journal $journal $state
  if(Test-Path -LiteralPath $backup) {Move-Item -LiteralPath $backup -Destination (Assert-Contained "$backup.retained.$([guid]::NewGuid().ToString('N'))" $tools)}
  if(Test-Path -LiteralPath $Directory) {Move-Item -LiteralPath $Directory -Destination $backup}
  $state.phase='backed-up';Write-Journal $journal $state
  try {
    Move-Item -LiteralPath $stage -Destination $Directory
    if(!(Test-Tree $Directory $Zip -Diagnose)) {throw 'Activated dependency failed verification.'}
    $state.phase='activated';Write-Journal $journal $state
    Remove-Item -LiteralPath $journal -Force
  } catch {
    if(Test-Tree $backup $Zip) {
      if(Test-Path -LiteralPath $Directory) {Move-Item -LiteralPath $Directory -Destination (Assert-Contained "$Directory.invalid.$([guid]::NewGuid().ToString('N'))" $tools)}
      Move-Item -LiteralPath $backup -Destination $Directory
    }
    throw
  }
}




