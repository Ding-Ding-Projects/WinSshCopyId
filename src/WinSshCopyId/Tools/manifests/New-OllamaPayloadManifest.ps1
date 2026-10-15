param(
    [ValidateSet('x64')][string]$Architecture = 'x64',
    [string]$OutputDirectory = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
$version = '0.40.4'
$assets = @{
    x64 = @{ Name = 'ollama-windows-amd64.zip'; Bytes = 1469916992; Sha256 = '7485A1172F8527DB4342A63521B9342FC466185FF0855DA378CC864ECF6354CF' }
}
$asset = $assets[$Architecture]
$cache = Join-Path $env:LOCALAPPDATA "WinSshCopyId\BuildCache\ollama-v$version"
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$archive = Join-Path $cache $asset.Name
if (-not (Test-Path -LiteralPath $archive)) {
    & gh release download "v$version" --repo ollama/ollama --pattern $asset.Name --dir $cache
    if ($LASTEXITCODE -ne 0) { throw 'Official release download failed.' }
}
if ((Get-Item -LiteralPath $archive).Length -ne $asset.Bytes -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $asset.Sha256) { throw 'Official payload length or digest mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $members = @($zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') } | ForEach-Object {
        $entry = $_
        if ($entry.FullName.Contains('..') -or $entry.FullName.Contains(':') -or $entry.FullName.StartsWith('/')) { throw 'Unsafe archive member.' }
        $stream = $entry.Open()
        try { [ordered]@{ RelativePath = $entry.FullName; Length = $entry.Length; Sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } }
        finally { $stream.Dispose() }
    })
    $executable = Join-Path $cache "ollama-$Architecture-signature-check.exe"
    if (Test-Path -LiteralPath $executable) { Remove-Item -LiteralPath $executable }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry('ollama.exe'), $executable)
    try {
        $signature = Get-AuthenticodeSignature -LiteralPath $executable
        if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate.Subject.StartsWith('CN=Ollama Inc., O=Ollama Inc.,')) { throw 'Official executable publisher verification failed.' }
        $manifest = [ordered]@{
            Version = $version; Architecture = $Architecture
            Source = "https://github.com/ollama/ollama/releases/download/v$version/$($asset.Name)"
            ArchiveBytes = $asset.Bytes; ArchiveSha256 = $asset.Sha256; Members = $members
            PublisherRequirement = 'Valid Authenticode signature; exact signer subject and thumbprint must match SignatureObservation'
            SourceRelease = "https://github.com/ollama/ollama/releases/tag/v$version"
            LicenseSource = "https://github.com/ollama/ollama/blob/v$version/LICENSE"
            License = 'MIT; preserve all included third-party license files'
            SignatureObservation = [ordered]@{ Status = 'Valid'; Subject = $signature.SignerCertificate.Subject; Thumbprint = $signature.SignerCertificate.Thumbprint }
            ArchiveVerification = 'Downloaded official release asset; local SHA-256 matches release asset digest; every file member hashed from ZIP stream'
            RuntimeVerified = $false
        }
        New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory "ollama-$version-windows-$Architecture.json") -Encoding utf8
        Write-Output "Verified Ollama $version $Architecture archive and $($members.Count) members; executable not launched."
    }
    finally { if (Test-Path -LiteralPath $executable) { Remove-Item -LiteralPath $executable } }
}
finally { $zip.Dispose() }

