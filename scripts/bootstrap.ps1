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
  $digest=(Get-FileHash $Zip -Algorithm SHA256).Hash
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
function Test-Tree([string]$Directory,[string]$Zip) {
  if(!(Test-Path -LiteralPath $Directory -PathType Container)) {return $false}
  try {
    $null=Assert-Contained $Directory $tools
    $expected=@(Get-ArchiveInventory $Zip)
    $actual=@(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force)
    if($actual.Count -ne $expected.Count) {return $false}
    foreach($entry in $expected) {
      $path=Assert-Contained (Join-Path $Directory $entry.path) $Directory
      if(!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $entry.bytes -or (Get-FileHash $path -Algorithm SHA256).Hash -ne $entry.sha256) {return $false}
    }
    return $true
  } catch {return $false}
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
      $validJournal=($state.active -eq $Directory -and $state.backup -eq $backup -and $state.archiveSha256 -eq (Get-FileHash $Zip -Algorithm SHA256).Hash -and $state.phase -in @('staged','backed-up','activated'))
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
  if(!(Test-Path (Join-Path $stage $Required)) -or !(Test-Tree $stage $Zip)) {throw 'Extracted dependency verification failed.'}
  $state=@{active=$Directory;backup=$backup;stage=$stage;archiveSha256=(Get-FileHash $Zip -Algorithm SHA256).Hash;phase='staged'}
  Write-Journal $journal $state
  if(Test-Path -LiteralPath $backup) {Move-Item -LiteralPath $backup -Destination (Assert-Contained "$backup.retained.$([guid]::NewGuid().ToString('N'))" $tools)}
  if(Test-Path -LiteralPath $Directory) {Move-Item -LiteralPath $Directory -Destination $backup}
  $state.phase='backed-up';Write-Journal $journal $state
  try {
    Move-Item -LiteralPath $stage -Destination $Directory
    if(!(Test-Tree $Directory $Zip)) {throw 'Activated dependency failed verification.'}
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
