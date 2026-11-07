param([string]$FfmpegPath, [string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$vendorDir = Join-Path $projectRoot 'vendor'
$buildDir = if ($BuildDirectory) { [IO.Path]::GetFullPath($BuildDirectory) } else { Join-Path $projectRoot 'artifacts' }
$releaseDir = Join-Path $buildDir 'QIPCConverter-Windows'
New-Item -ItemType Directory -Force -Path $vendorDir,$buildDir,$releaseDir | Out-Null

if (-not $FfmpegPath) {
    $FfmpegPath = Join-Path $vendorDir 'ffmpeg.exe'
    if (-not (Test-Path -LiteralPath $FfmpegPath)) {
        & (Join-Path $projectRoot 'tools\fetch-ffmpeg.ps1')
    }
}
$FfmpegPath = (Resolve-Path -LiteralPath $FfmpegPath).Path
$expected = '2ce797a0f88d7f067180338fb227f7b1928ea727bd9a4d7a1d022f7c52af71a3'
if ((Get-FileHash -LiteralPath $FfmpegPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw 'FFmpeg SHA-256 does not match the tested 7.1 build.'
}
$packed = Join-Path $vendorDir 'ffmpeg.gz'
$sourceStream = [IO.File]::OpenRead($FfmpegPath)
try {
    $packedStream = [IO.File]::Create($packed)
    try {
        $gzip = New-Object IO.Compression.GZipStream($packedStream, [IO.Compression.CompressionMode]::Compress)
        try { $sourceStream.CopyTo($gzip) } finally { $gzip.Dispose() }
    } finally { $packedStream.Dispose() }
} finally { $sourceStream.Dispose() }

Add-Type -AssemblyName System.Drawing
$bitmap = New-Object Drawing.Bitmap(64,64)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$blue = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(43,95,232))
$white = New-Object Drawing.SolidBrush([Drawing.Color]::White)
$graphics.FillRectangle($blue,0,0,64,64)
$triangle = [Drawing.Point[]]@([Drawing.Point]::new(24,16),[Drawing.Point]::new(24,48),[Drawing.Point]::new(47,32))
$graphics.FillPolygon($white,$triangle)
# An ICO can contain a PNG payload. Avoid native icon handles during the build.
$png = New-Object IO.MemoryStream
$bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
$iconStream = [IO.File]::Create((Join-Path $buildDir 'app.ico'))
$iconWriter = New-Object IO.BinaryWriter($iconStream)
try {
    $iconWriter.Write([uint16]0); $iconWriter.Write([uint16]1); $iconWriter.Write([uint16]1)
    $iconWriter.Write([byte]64); $iconWriter.Write([byte]64); $iconWriter.Write([byte]0); $iconWriter.Write([byte]0)
    $iconWriter.Write([uint16]1); $iconWriter.Write([uint16]32); $iconWriter.Write([uint32]$png.Length); $iconWriter.Write([uint32]22)
    $iconWriter.Write($png.ToArray())
} finally {
    $iconWriter.Dispose(); $png.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $blue.Dispose(); $white.Dispose()
}

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 compiler not found. Build this project on 64-bit Windows.' }
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$commonArgs = @('/nologo','/codepage:65001','/optimize+','/platform:x64','/reference:System.dll','/reference:System.Core.dll',
    '/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll',"/resource:$packed,QipcConverter.ffmpeg.gz")
$appExe = Join-Path $releaseDir 'QIPCConverter.exe'
& $compiler @commonArgs '/target:winexe' "/out:$appExe" "/win32icon:$(Join-Path $buildDir 'app.ico')" "/win32manifest:$(Join-Path $projectRoot 'src\app.manifest')" @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }

$testExe = Join-Path $buildDir 'QipcConverter.Tests.exe'
& $compiler @commonArgs '/target:exe' '/main:QipcConverter.Tests' "/out:$testExe" @sourceFiles (Join-Path $projectRoot 'tests\Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\사용방법.txt') -Destination $releaseDir -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $releaseDir -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses\GPL-3.0.txt') -Destination $releaseDir -Force
Write-Output "Built: $appExe"
Write-Output "Tests: $testExe"
