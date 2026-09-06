param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repoRoot
try {
    if (-not $SkipTests) {
        dotnet run --project tests\ToolkitLauncher.Tests -c Release -- --ui artifacts\ui-review
        if ($LASTEXITCODE -ne 0) { throw 'Regression or UI checks failed.' }
    }
    dotnet publish src\ToolkitLauncher\ToolkitLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts\publish\win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $releaseDirectory = Join-Path $repoRoot 'artifacts\release'
    New-Item -ItemType Directory -Force -Path $releaseDirectory | Out-Null
    $executable = Join-Path $releaseDirectory 'Production Toolkit.exe'
    Copy-Item -LiteralPath (Join-Path $repoRoot 'artifacts\publish\win-x64\Production Toolkit.exe') -Destination $executable -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $releaseDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE.md') -Destination $releaseDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $releaseDirectory -Force
    $version = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    $archive = Join-Path $releaseDirectory "Production-Toolkit-$version-win-x64.zip"
    Compress-Archive -LiteralPath @($executable, (Join-Path $releaseDirectory 'README.md'), (Join-Path $releaseDirectory 'LICENSE.md'), (Join-Path $releaseDirectory 'THIRD-PARTY-NOTICES.md')) -DestinationPath $archive -Force
    $checksums = @($executable, $archive) | ForEach-Object { ((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()) + '  ' + [IO.Path]::GetFileName($_) }
    [IO.File]::WriteAllLines((Join-Path $releaseDirectory 'SHA256SUMS.txt'), $checksums)
    Write-Host "Standalone executable: $executable"
}
finally { Pop-Location }
