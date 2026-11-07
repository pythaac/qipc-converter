param([string]$Destination, [string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $projectRoot 'artifacts\packages' }
$Destination = [IO.Path]::GetFullPath($Destination)
$buildRoot = if ($BuildDirectory) { [IO.Path]::GetFullPath($BuildDirectory) } else { Join-Path $projectRoot 'artifacts' }
$builtRelease = Join-Path $buildRoot 'QIPCConverter-Windows'
if (-not (Test-Path -LiteralPath (Join-Path $builtRelease 'QIPCConverter.exe'))) { throw 'Run build.ps1 first.' }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$portableDir = Join-Path $Destination 'QIPCConverter-Windows'
New-Item -ItemType Directory -Force -Path $portableDir | Out-Null
Get-ChildItem -LiteralPath $builtRelease -File | Copy-Item -Destination $portableDir -Force
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$portableZip = Join-Path $Destination 'QIPCConverter-Windows.zip'
$sourceZip = Join-Path $Destination 'QipcConverter-source.zip'
# Open with CreateNew so packaging never overwrites an existing archive.
$portableStream = [IO.File]::Open($portableZip, [IO.FileMode]::CreateNew)
$portableArchive = New-Object IO.Compression.ZipArchive($portableStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($releaseFile in (Get-ChildItem -LiteralPath $portableDir -File)) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($portableArchive, $releaseFile.FullName, ('QIPCConverter-Windows/' + $releaseFile.Name), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $portableArchive.Dispose(); $portableStream.Dispose() }

$sourceStream = [IO.File]::Open($sourceZip, [IO.FileMode]::CreateNew)
$sourceArchive = New-Object IO.Compression.ZipArchive($sourceStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    $allowedDirs = @('src','tests','tools','docs','licenses')
    $sourceFiles = @(Get-ChildItem -LiteralPath $projectRoot -File -Force)
    foreach ($directory in $allowedDirs) { $sourceFiles += Get-ChildItem -LiteralPath (Join-Path $projectRoot $directory) -File -Recurse }
    $sourceFiles += Get-Item -LiteralPath (Join-Path $projectRoot 'vendor\README.md')
    foreach ($sourceFile in $sourceFiles) {
        $relativeName = $sourceFile.FullName.Substring($projectRoot.Length + 1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($sourceArchive, $sourceFile.FullName, ('QipcConverter/' + $relativeName), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $sourceArchive.Dispose(); $sourceStream.Dispose() }
Write-Output $portableDir
Write-Output $portableZip
Write-Output $sourceZip
