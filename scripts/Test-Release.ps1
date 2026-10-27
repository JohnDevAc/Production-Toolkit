param([string]$ReleaseDirectory = (Join-Path $PSScriptRoot '../artifacts/release'))
$ErrorActionPreference = 'Stop'
$version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$payloads = @("Production-Toolkit-$version-win-x64-Setup.exe", 'Production.Toolkit.exe', "Production-Toolkit-$version-win-x64.zip")
$expected = $payloads + @('SHA256SUMS.txt', 'README.md', 'INTEROPERABILITY.md', 'LICENSE.md', 'THIRD-PARTY-NOTICES.md')
$actual = @(Get-ChildItem -LiteralPath $ReleaseDirectory | ForEach-Object Name)
if (Compare-Object $expected $actual -CaseSensitive) { throw 'Release files do not match the intended uploaded asset names.' }
if (@(Get-ChildItem -LiteralPath $ReleaseDirectory -Directory).Count) { throw 'Release uploads must contain only files.' }
$entries = @(Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'SHA256SUMS.txt') | ForEach-Object {
    if ($_ -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9._-]+)$') { throw 'Invalid checksum entry or unsafe upload filename.' }
    [pscustomobject]@{ Hash = $Matches[1]; Name = $Matches[2] }
})
if (Compare-Object $payloads @($entries.Name) -CaseSensitive) { throw 'Checksum filenames do not match release payloads.' }
foreach ($entry in $entries) {
    if ((Get-FileHash -LiteralPath (Join-Path $ReleaseDirectory $entry.Name) -Algorithm SHA256).Hash -ne $entry.Hash) {
        throw ('Checksum mismatch: ' + $entry.Name)
    }
}
if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    $expectedNumeric = ($version -split '-', 2)[0] + '.0'
    foreach ($name in $payloads[0..1]) {
        $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $ReleaseDirectory $name))
        if ($metadata.FileVersion -ne $expectedNumeric -or ($metadata.ProductVersion -split '\+', 2)[0] -ne $version) {
            throw ('Binary numeric/semantic version differs from its release channel: ' + $name)
        }
    }
}
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $ReleaseDirectory $payloads[2]))
try {
    $zipNames = @($zip.Entries | ForEach-Object FullName)
    if (Compare-Object @('Production Toolkit.exe', 'README.md', 'INTEROPERABILITY.md', 'LICENSE.md', 'THIRD-PARTY-NOTICES.md') $zipNames -CaseSensitive) {
        throw 'Portable ZIP contents do not retain the installed executable and documentation names.'
    }
    foreach ($entry in $zip.Entries) {
        $publishedName = if ($entry.Name -eq 'Production Toolkit.exe') { 'Production.Toolkit.exe' } else { $entry.Name }
        $published = Join-Path $ReleaseDirectory $publishedName
        $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($entry.Length -ne (Get-Item -LiteralPath $published).Length -or $hash -ne (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash) {
            throw ('Portable ZIP entry differs from its published counterpart: ' + $entry.Name)
        }
    }
}
finally { $zip.Dispose() }
Write-Host 'PASS: Release upload filenames, all 3 checksums, binary channel metadata on Windows and all 5 ZIP entry hashes match the artifact contract.'
