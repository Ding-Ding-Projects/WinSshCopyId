function ConvertTo-ReleaseArgument([string]$Value) {
  return '"'+(($Value -replace '(\\*)"','$1$1\"') -replace '(\\+)$','$1$1')+'"'
}
function Invoke-ReleaseProcess([string]$File,[string[]]$Arguments,[int]$TimeoutSeconds=120) {
  $start=New-Object Diagnostics.ProcessStartInfo
  $start.FileName=$File;$start.Arguments=($Arguments|ForEach-Object {ConvertTo-ReleaseArgument $_}) -join ' '
  $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
  $process=New-Object Diagnostics.Process;$process.StartInfo=$start
  try {
    if(!$process.Start()) {throw 'Release CLI process could not start.'}
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    if(!$process.WaitForExit($TimeoutSeconds*1000)) {$process.Kill();$process.WaitForExit();throw 'Release CLI process exceeded its bounded deadline.'}
    $output=$stdout.GetAwaiter().GetResult();$diagnostic=$stderr.GetAwaiter().GetResult()
    if($output.Length -gt 1048576 -or $diagnostic.Length -gt 65536) {throw 'Release CLI response exceeded its bound.'}
    return [pscustomobject]@{ExitCode=$process.ExitCode;Output=$output;Error=$diagnostic}
  } finally {$process.Dispose()}
}
function Invoke-Gh([string[]]$Arguments) {
  $result=Invoke-ReleaseProcess $script:ReleaseGhExecutable $Arguments
  if($result.ExitCode -ne 0) {throw "GitHub CLI operation failed with exit $($result.ExitCode): $($Arguments[0])"}
  return $result.Output
}
function Assert-ReleaseAbsent([string]$Tag,[string]$Repository) {
  $result=Invoke-ReleaseProcess $script:ReleaseGhExecutable @('release','view',$Tag,'--repo',$Repository,'--json','tagName')
  if($result.ExitCode -eq 0) {throw 'Immutable release tag already exists; no overwrite is permitted.'}
  if($result.ExitCode -ne 1 -or $result.Output.Trim().Length -ne 0 -or $result.Error.Trim() -cne 'release not found') {throw "Release existence could not be established; CLI exit $($result.ExitCode)."}
}
