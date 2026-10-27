param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$ExpectedSHA256,
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new extraction directory; frozen payloads cannot be overwritten.' }
if ($ExpectedSHA256 -cnotmatch '^[0-9a-f]{64}$' -or $SourceCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'Expected exact digest and source commit.' }
if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $ExpectedSHA256) { throw 'Frozen executable hash differs.' }
$productVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if ($productVersion -ne ('1.4.0+' + $SourceCommit)) { throw 'Frozen executable source stamp differs.' }
$sdkVersion = (dotnet --version).Trim()
$sdkLine = dotnet --list-sdks | Where-Object { $_.StartsWith($sdkVersion + ' [') } | Select-Object -First 1
if (-not $sdkLine -or $sdkLine -notmatch '\[(.+)\]$') { throw 'Cannot locate current SDK host model.' }
$hostModel = Join-Path $Matches[1] ($sdkVersion + '/Microsoft.NET.HostModel.dll')
[void][Reflection.Assembly]::LoadFrom($hostModel)
[long]$headerOffset = 0
if (-not [Microsoft.NET.HostModel.AppHost.HostWriter]::IsBundle($exe, [ref]$headerOffset)) { throw 'Frozen executable is not a .NET bundle.' }
$stream = [IO.File]::OpenRead($exe)
$reader = [IO.BinaryReader]::new($stream)
try {
    if ($headerOffset -le 0 -or $headerOffset -ge $stream.Length) { throw 'Invalid bundle header offset.' }
    $stream.Position = $headerOffset
    $major = $reader.ReadUInt32(); $minor = $reader.ReadUInt32(); $count = $reader.ReadInt32(); $bundleId = $reader.ReadString()
    if ($major -ne 6 -or $minor -ne 0 -or $count -le 0 -or $count -gt 10000) { throw 'Unsupported bundle format.' }
    # .NET v6 manifest layout: four Int64 metadata locations/sizes, UInt64 flags,
    # then offset/size/compressed-size/type and BinaryReader string for each entry.
    # https://github.com/dotnet/runtime/blob/v8.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/Manifest.cs
    # https://github.com/dotnet/runtime/blob/v8.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/FileEntry.cs
    $metadataLocations = @(1..4 | ForEach-Object { $reader.ReadInt64() })
    $flags = $reader.ReadUInt64()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $entries = @(for ($i = 0; $i -lt $count; $i++) {
        $offset = $reader.ReadInt64(); $size = $reader.ReadInt64(); $compressed = $reader.ReadInt64(); $type = $reader.ReadByte(); $name = $reader.ReadString()
        $stored = if ($compressed -gt 0) { $compressed } else { $size }
        if ($offset -lt 0 -or $size -lt 0 -or $compressed -lt 0 -or $stored -gt [int]::MaxValue -or $offset + $stored -gt $headerOffset -or $type -notin 1,2,3,4) { throw 'Invalid bundle entry bounds/type.' }
        if ([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::IsPathRooted($name) -or $name.Contains('\') -or $name.Contains(':') -or
            @($name.Split('/') | Where-Object { $_ -in '', '.', '..' }).Count -gt 0 -or -not $seen.Add($name)) { throw 'Invalid or duplicate bundle path.' }
        [pscustomobject]@{ name=$name; offset=$offset; size=$size; compressedSize=$compressed; type=$type; storedSize=$stored }
    })
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $payload = @(foreach ($entry in $entries) {
        # Ordinary app launch metadata is evidence only; QA has its own guarded host.
        $folder = if ($entry.type -in 3,4) { 'metadata' } else { 'launcher' }
        $relative = $folder + '/' + $entry.name
        $destination = [IO.Path]::GetFullPath((Join-Path $output $relative))
        if (-not $destination.StartsWith($output.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Bundle path escapes extraction root.' }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        $stream.Position = $entry.offset
        $bytes = $reader.ReadBytes([int]$entry.storedSize)
        if ($bytes.Length -ne $entry.storedSize) { throw 'Truncated bundle entry.' }
        if ($entry.compressedSize -gt 0) {
            $inputStream = [IO.MemoryStream]::new($bytes, $false)
            $inflate = [IO.Compression.DeflateStream]::new($inputStream, [IO.Compression.CompressionMode]::Decompress)
            $target = [IO.File]::Open($destination, [IO.FileMode]::CreateNew)
            try { $inflate.CopyTo($target) } finally { $target.Dispose(); $inflate.Dispose(); $inputStream.Dispose() }
        } else { [IO.File]::WriteAllBytes($destination, $bytes) }
        if ((Get-Item -LiteralPath $destination).Length -ne $entry.size) { throw 'Decompressed entry length differs.' }
        [pscustomobject]@{ name=$entry.name; file=$relative; size=$entry.size; offset=$entry.offset; compressedSize=$entry.compressedSize; sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
} finally { $reader.Dispose(); $stream.Dispose() }
foreach ($name in @('Production Toolkit.dll','ToolkitLauncher.Core.dll')) {
    if ((Get-Item -LiteralPath (Join-Path $output ('launcher/' + $name))).VersionInfo.ProductVersion -ne $productVersion) { throw 'Extracted assembly source stamp differs.' }
}
if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $ExpectedSHA256) { throw 'Frozen executable changed during extraction.' }
$payload | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'bundle-payload.json') -Encoding utf8
[ordered]@{
    productionSourceCommit=$SourceCommit; productionExecutable=$exe; productionExecutableSHA256=$ExpectedSHA256
    productionProductVersion=$productVersion; productionRebuilt=$false; extractionOnly=$true
    bundleVersion="$major.$minor"; bundleId=$bundleId; bundleEntries=$count; hostModel=$hostModel
    extractedAtUtc=[DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'build-info.json') -Encoding utf8
$hashes = Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName | ForEach-Object {
    ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) + '  ' + [IO.Path]::GetRelativePath($output,$_.FullName).Replace('\','/')
}
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'),$hashes)
Write-Host "PASS: extracted $count exact entries from frozen $ExpectedSHA256; production was not rebuilt or executed."
