#Requires -Version 7.4
# Packages both x64 single-file distributions and verifies unpacked builds with isolated smoke tests.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
    [string]$PublishRoot = "$PSScriptRoot/../src/WindowShuttle.App/bin/Release",
    [string]$OutputDirectory = '.'
)
$ErrorActionPreference = 'Stop'
$PublishRoot = (Resolve-Path -LiteralPath $PublishRoot).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
$assets = @()
foreach ($form in '', '-needs-dotnet10') {
    $directory = Join-Path $PublishRoot "publish$form"
    $exe = Join-Path $directory 'WindowShuttle.exe'
    # Both ZIPs and the raw download promise a single-file program. Fail if a new dependency
    # introduces sidecars instead of silently leaving them out of the release.
    $files = @(Get-ChildItem -LiteralPath $directory -Recurse -Force -File)
    if ($files.Count -ne 1 -or $files[0].FullName -ne $exe) { throw "Expected only WindowShuttle.exe in $directory" }
    $stream = [IO.File]::OpenRead($exe)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid PE: $exe" }
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw "Expected x64 PE: $exe" }
    } finally { $stream.Dispose() }

    $zip = Join-Path $outputRoot "WindowShuttle-$Tag-win-x64$form.zip"
    Compress-Archive -LiteralPath $exe -DestinationPath $zip -Force
    $sandbox = Join-Path ([IO.Path]::GetTempPath()) ("windowshuttle-package-$([guid]::NewGuid())")
    try {
        $unpacked = Join-Path $sandbox 'unpacked'
        $temp = Join-Path $sandbox 'temp'
        $data = Join-Path $sandbox 'data'
        New-Item -ItemType Directory -Path $unpacked,$temp,$data -Force | Out-Null
        Expand-Archive -LiteralPath $zip -DestinationPath $unpacked
        $unpackedExe = Join-Path $unpacked 'WindowShuttle.exe'
        if ((Get-FileHash -LiteralPath $exe).Hash -ne (Get-FileHash -LiteralPath $unpackedExe).Hash) { throw "ZIP content mismatch: $zip" }
        $environment = @{ TEMP = $temp; TMP = $temp; APPDATA = $data; LOCALAPPDATA = $data }
        $process = Start-Process -FilePath $unpackedExe -WorkingDirectory $unpacked -ArgumentList '--smoke' -Environment $environment -WindowStyle Hidden -PassThru
        try {
            if (-not $process.WaitForExit(60000)) { $process.Kill(); $process.WaitForExit(); throw "Packaged smoke timed out: $zip" }
            $marker = Join-Path $temp 'windowshuttle-smoke.txt'
            if (-not (Test-Path -LiteralPath $marker)) { throw "Packaged smoke wrote no marker: $zip" }
            $report = (Get-Content -LiteralPath $marker -Raw).Trim()
            if ($report -ne 'OK' -or $process.ExitCode) { throw "Packaged smoke failed: $zip`n$report" }
        } finally { $process.Dispose() }
        $assets += $zip
    } finally {
        $resolved = [IO.Path]::GetFullPath($sandbox)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        if ([IO.Path]::GetDirectoryName($resolved) -ne $tempRoot) { throw 'Unexpected package cleanup directory' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
# Keep the existing raw framework-dependent download, with the same bytes as its ZIP.
$raw = Join-Path $outputRoot 'WindowShuttle.exe'
$source = Join-Path $PublishRoot 'publish-needs-dotnet10/WindowShuttle.exe'
if ($raw -ne $source) { Copy-Item -LiteralPath $source -Destination $raw -Force }
$assets += $raw
$checksums = Join-Path $outputRoot 'SHA256SUMS.txt'
# An explicit inventory excludes unrelated ZIPs and writes LF for sha256sum on other platforms.
$lines = $assets | Get-FileHash -Algorithm SHA256 | ForEach-Object { "{0}  {1}" -f $_.Hash.ToLowerInvariant(), (Split-Path $_.Path -Leaf) }
[IO.File]::WriteAllText($checksums, ($lines -join "`n") + "`n")
if ($env:GITHUB_OUTPUT) {
    $files = (@($assets) + $checksums) | ForEach-Object { $_.Replace('\', '/') }
    [IO.File]::AppendAllText($env:GITHUB_OUTPUT, ((@('files<<END_ASSETS') + $files + 'END_ASSETS') -join "`n") + "`n")
}
Write-Host "Verified both x64 packages with --smoke. Checksums: $checksums"
