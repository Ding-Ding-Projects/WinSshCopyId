$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'release-cli.ps1')
$root=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path $root ('.tools/release-cli-fixture-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $fixture|Out-Null
$script:ReleaseGhExecutable=Join-Path $fixture 'fake-gh.exe'
$code=@'
using System;
public class FakeGh {
 public static int Main(string[] args) {
  string mode=Environment.GetEnvironmentVariable("RELEASE_CLI_FIXTURE");
  if(mode=="args") {foreach(string arg in args) Console.WriteLine(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(arg)));return 0;}
  if(mode=="timeout") {System.Threading.Thread.Sleep(5000);return 0;}
  if(args[1]=="create") { Console.Error.WriteLine("creation rejected");return 1; }
  if(mode=="missing") {Console.Error.WriteLine("release not found");return 1;}
  if(mode=="auth") {Console.Error.WriteLine("HTTP 401: authentication required");return 1;}
  if(mode=="network") {Console.Error.WriteLine("connection failed");return 1;}
  if(mode=="misleading") {Console.Error.WriteLine("release not found\nHTTP 401");return 1;}
  Console.WriteLine("{\"tagName\":\"existing\"}");Console.Error.WriteLine("informational stderr");return 0;
 }
}
'@
Add-Type -TypeDefinition $code -OutputAssembly $script:ReleaseGhExecutable -OutputType ConsoleApplication
$old=$env:RELEASE_CLI_FIXTURE
try {
 $env:RELEASE_CLI_FIXTURE='missing';Assert-ReleaseAbsent tag owner/repo
 foreach($mode in @('auth','network','misleading','exists')) {
  $env:RELEASE_CLI_FIXTURE=$mode;$blocked=$false
  try {Assert-ReleaseAbsent tag owner/repo} catch {$blocked=$true}
  if(!$blocked) {throw "Unexpected absence accepted: $mode"}
 }
 $blocked=$false;try {Invoke-Gh @('release','create','tag')}catch{$blocked=$true};if(!$blocked){throw 'Creation failure accepted.'}
 $env:RELEASE_CLI_FIXTURE='exists';$result=Invoke-Gh @('release','view','tag');if(($result|ConvertFrom-Json).tagName -ne 'existing'){throw 'Successful stderr operation rejected.'}
 $env:RELEASE_CLI_FIXTURE='args';$values=@('release','view','two words','quote"value','C:\trailing\');$echo=Invoke-ReleaseProcess $script:ReleaseGhExecutable $values
 $decoded=@($echo.Output.Trim() -split "`r?`n"|ForEach-Object {[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_))})
 if(($decoded -join '|') -cne ($values -join '|')) {throw 'Native argument quoting changed a value.'}
 $env:RELEASE_CLI_FIXTURE='timeout';$blocked=$false;try {Invoke-ReleaseProcess $script:ReleaseGhExecutable @('release','view','tag') 1}catch{$blocked=$true};if(!$blocked){throw 'Process deadline was not enforced.'}
 $oldPath=$env:PATH
 try {
  $one=Join-Path $fixture 'first path';$two=Join-Path $fixture 'second path'
  New-Item -ItemType Directory -Force $one,$two|Out-Null
  Copy-Item $script:ReleaseGhExecutable (Join-Path $one 'delivery-fixture.exe')
  Copy-Item $script:ReleaseGhExecutable (Join-Path $two 'delivery-fixture.exe')
  $env:PATH="$one;$two;$oldPath"
  if(@(Get-Command delivery-fixture.exe -CommandType Application).Count -lt 2) {throw 'Duplicate PATH fixture did not expose multiple applications.'}
  $resolved=Resolve-ReleaseExecutable 'delivery-fixture.exe'
  if($resolved -cne (Join-Path $one 'delivery-fixture.exe')) {throw 'Resolver did not pin the first application.'}
  $env:RELEASE_CLI_FIXTURE='exists';$proof=Invoke-ReleaseProcess $resolved @('release','view','tag')
  if($proof.ExitCode -ne 0) {throw 'Pinned duplicate-PATH application failed to launch.'}
 } finally {$env:PATH=$oldPath}
 foreach($name in @('git','gh')) {
  $resolved=Resolve-ReleaseExecutable $name
  $proof=Invoke-ReleaseProcess $resolved @('--version')
  if($proof.ExitCode -ne 0 -or !$proof.Output.Trim()) {throw "Real tool resolution failed: $name"}
 }
 Write-Output 'PASS: PS5.1 native not-found handling, auth/network/mixed-error rejection, existing-tag protection, creation failure propagation, successful stderr capture, exact argument quoting bounded deadline, duplicate-PATH first-application resolution and real git/gh launch'
} finally {$env:RELEASE_CLI_FIXTURE=$old}


