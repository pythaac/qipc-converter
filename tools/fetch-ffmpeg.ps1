$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$vendorDir = Join-Path $projectRoot 'vendor'
New-Item -ItemType Directory -Force -Path $vendorDir | Out-Null
$metadata = Invoke-RestMethod -Uri 'https://pypi.org/pypi/imageio-ffmpeg/0.6.0/json' -TimeoutSec 30
$wheel = @($metadata.urls | Where-Object filename -eq 'imageio_ffmpeg-0.6.0-py3-none-win_amd64.whl')
if ($wheel.Count -ne 1) { throw 'Expected Windows x64 wheel was not found.' }
$archive = Join-Path $vendorDir 'imageio_ffmpeg-0.6.0.whl'
Invoke-WebRequest -UseBasicParsing -Uri $wheel[0].url -OutFile $archive -TimeoutSec 60
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $wheel[0].digests.sha256) { throw 'Wheel SHA-256 mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $entry = $zip.GetEntry('imageio_ffmpeg/binaries/ffmpeg-win-x86_64-v7.1.exe')
    if (-not $entry) { throw 'FFmpeg executable not found in wheel.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $vendorDir 'ffmpeg.exe'), $true)
} finally { $zip.Dispose() }
$binaryHash = (Get-FileHash -LiteralPath (Join-Path $vendorDir 'ffmpeg.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($binaryHash -ne '2ce797a0f88d7f067180338fb227f7b1928ea727bd9a4d7a1d022f7c52af71a3') { throw 'FFmpeg SHA-256 mismatch.' }
Write-Output 'Verified FFmpeg 7.1 Windows x64 binary is ready.'
