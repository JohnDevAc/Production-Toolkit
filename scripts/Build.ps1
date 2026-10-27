param([switch]$SkipTests, [string]$OutputRoot = (Join-Path $PSScriptRoot '../artifacts'))
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputRoot)
$publishDirectory = Join-Path $output 'publish/win-x64'
$releaseDirectory = Join-Path $output 'release'
if ($PSBoundParameters.ContainsKey('OutputRoot') -and ((Test-Path -LiteralPath $publishDirectory) -or (Test-Path -LiteralPath $releaseDirectory))) {
    throw 'Choose a new output root for a clean channel build; existing packages are preserved.'
}
Push-Location $repoRoot
try {
    if (-not $SkipTests) {
        dotnet run --project tests\ToolkitLauncher.Tests -c Release -- --ui (Join-Path $output 'ui-review')
        if ($LASTEXITCODE -ne 0) { throw 'Regression or UI checks failed.' }
    }
    dotnet publish src\ToolkitLauncher\ToolkitLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    New-Item -ItemType Directory -Force -Path $releaseDirectory | Out-Null
    # This flat generated directory is the exact upload set. Remove stale build
    # files so old versions and GitHub-renamed portable names cannot leak into it.
    Get-ChildItem -LiteralPath $releaseDirectory -File | Remove-Item -Force
    $executable = Join-Path $releaseDirectory 'Production.Toolkit.exe'
    Copy-Item -LiteralPath (Join-Path $publishDirectory 'Production Toolkit.exe') -Destination $executable -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $releaseDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'INTEROPERABILITY.md') -Destination $releaseDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE.md') -Destination $releaseDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $releaseDirectory -Force
    $version = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    $compiler = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1')
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion
    & $compiler ('/DAppVersion=' + $version) ('/DAppFileVersion=' + $fileVersion) ('/DPublishDirectory=' + $publishDirectory) ('/DReleaseDirectory=' + $releaseDirectory) (Join-Path $repoRoot 'installer\ProductionToolkit.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Windows installer build failed.' }
    $setup = Join-Path $releaseDirectory "Production-Toolkit-$version-win-x64-Setup.exe"
    $archive = Join-Path $releaseDirectory "Production-Toolkit-$version-win-x64.zip"
    Compress-Archive -LiteralPath @((Join-Path $publishDirectory 'Production Toolkit.exe'), (Join-Path $releaseDirectory 'README.md'), (Join-Path $releaseDirectory 'INTEROPERABILITY.md'), (Join-Path $releaseDirectory 'LICENSE.md'), (Join-Path $releaseDirectory 'THIRD-PARTY-NOTICES.md')) -DestinationPath $archive -Force
    $checksums = @($setup, $executable, $archive) | ForEach-Object { ((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()) + '  ' + [IO.Path]::GetFileName($_) }
    [IO.File]::WriteAllLines((Join-Path $releaseDirectory 'SHA256SUMS.txt'), $checksums)
    & (Join-Path $PSScriptRoot 'Test-Release.ps1') -ReleaseDirectory $releaseDirectory
    Write-Host "Windows installer: $setup"
}
finally { Pop-Location }
