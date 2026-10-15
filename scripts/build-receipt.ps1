function Test-BuildReceipt([string]$ReceiptPath,[string]$Publish,[string]$Source,[string]$ManifestHash,[string]$Version,[string]$Runtime,[string]$SourceTree,[string]$IndexTree) {
  if(!(Test-Path -LiteralPath $ReceiptPath -PathType Leaf)) {return $false}
  try {
    $receipt=Get-Content -LiteralPath $ReceiptPath -Raw | ConvertFrom-Json
    if($receipt.packageId -ne $m.packageId -or $receipt.schemaVersion -ne 1 -or $receipt.sourceCommit -ne $Source -or $receipt.manifestSha256 -ne $ManifestHash -or $receipt.version -ne $Version -or $receipt.runtime -ne $Runtime -or $receipt.sourceTree -ne $SourceTree -or $receipt.indexTree -ne $IndexTree -or $receipt.command -ne 'build.bat /s' -or $receipt.publishDirectory -ne $m.publishDirectory -or $receipt.executable -ne $m.executable -or $receipt.project -ne $m.project) {return $false}
    $actual=@(Get-ChildItem -LiteralPath $Publish -Recurse -File -Force)
    if($actual.Count -ne @($receipt.files).Count -or $actual.Count -eq 0) {return $false}
    $seen=@{}
    foreach($file in $receipt.files) {
      if([IO.Path]::IsPathRooted($file.path) -or $file.path -match '(^|[\\/])\.\.([\\/]|$)|:') {return $false}
      $path=Assert-Contained (Join-Path $Publish $file.path) $Publish
      if($seen.ContainsKey($path)) {return $false};$seen[$path]=$true
      if(!(Test-Path -LiteralPath $path -PathType Leaf)) {return $false}
      if((Get-Item -LiteralPath $path -Force).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {return $false}
    }
    foreach($file in $actual) {if(!$seen.ContainsKey($file.FullName)) {return $false}}
    foreach($name in $m.requiredPayload) {if(!(Test-Path -LiteralPath (Join-Path $Publish $name) -PathType Leaf)) {return $false}}
    if((Get-FileHash -LiteralPath (Join-Path $Publish 'Update.exe') -Algorithm SHA256).Hash -ne $receipt.updaterSha256) {return $false}
    return $true
  } catch {return $false}
}


