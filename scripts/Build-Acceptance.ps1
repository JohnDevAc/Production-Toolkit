param(
    [string]$PackageManifest = (Join-Path $PSScriptRoot '../artifacts/lifecycle-candidate-packages-12.json'),
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$FrozenToolkitDirectory
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceCommit = 'dfb301ec45ba5941a22c0feb0192601f2640ad82'
$productionExecutableSHA256 = '5e5ca8676bd78ecc78d6d73c10b1220b2331356ebd1f4ab60d2b3e8b8cff42e0'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new output folder; frozen QA bundles are never overwritten.' }
$candidates = @(Get-Content -LiteralPath $PackageManifest -Raw | ConvertFrom-Json)
$versions = @{ environment = '3.1.0'; job = '0.8.16-dev.1'; 'pc-agent' = '0.7.5-dev.1'; resolume = '0.3.8' }
if ($candidates.Count -ne 4 -or @($candidates.product | Sort-Object -Unique).Count -ne 4) { throw 'Expected all four distinct candidate packages.' }
Push-Location $repo
try {
    git diff --quiet $sourceCommit -- src Directory.Build.props installer
    if ($LASTEXITCODE -ne 0) { throw "Production sources differ from frozen candidate $sourceCommit. Review and report before building." }
    $harnessCommit = (git rev-parse HEAD).Trim()
    $frozen = [IO.Path]::GetFullPath($FrozenToolkitDirectory)
    $frozenInfo = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $frozen) 'build-info.json') -Raw | ConvertFrom-Json
    if ($frozenInfo.productionSourceCommit -ne $sourceCommit) { throw 'Frozen assemblies belong to a different production source.' }
    if ($frozenInfo.extractionOnly -ne $true -or $frozenInfo.productionExecutableSHA256 -ne $productionExecutableSHA256 -or
        (Get-FileHash -LiteralPath $frozenInfo.productionExecutable -Algorithm SHA256).Hash -ne $productionExecutableSHA256) {
        throw 'QA12 requires assemblies extracted from the exact frozen production11 executable.'
    }
    $frozenChecksums = @{}
    foreach ($line in (Get-Content -LiteralPath (Join-Path (Split-Path -Parent $frozen) 'SHA256SUMS.txt'))) {
        $parts = $line -split '  ', 2
        $frozenChecksums[$parts[1]] = $parts[0]
    }
    $frozenPayload = @(Get-ChildItem -LiteralPath $frozen -File -Recurse | Where-Object Name -NotLike 'Toolkit.QA*' | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($frozen, $_.FullName).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $frozenChecksums['launcher/' + $relative]) { throw ('Frozen payload changed: ' + $relative) }
        [pscustomobject]@{ name = $relative; sha256 = $hash }
    })
    foreach ($name in @('Production Toolkit.dll', 'ToolkitLauncher.Core.dll')) {
        if (-not ($frozenPayload | Where-Object name -EQ $name)) { throw ('Missing frozen production assembly: ' + $name) }
    }
    New-Item -ItemType Directory -Path $output | Out-Null
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $frozen) 'build-info.json') -Destination (Join-Path $output 'production-extraction.json')
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $frozen) 'bundle-payload.json') -Destination (Join-Path $output 'production-bundle-payload.json')
    $packageFolder = Join-Path $output 'packages'
    New-Item -ItemType Directory -Path $packageFolder | Out-Null
    $packages = foreach ($candidate in $candidates) {
        if (-not $versions.ContainsKey($candidate.product)) { throw 'Unexpected candidate product.' }
        $file = Get-Item -LiteralPath $candidate.path
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $candidate.sha256) { throw ('Candidate changed: ' + $candidate.product) }
        Copy-Item -LiteralPath $file.FullName -Destination $packageFolder
        [ordered]@{ product = $candidate.product; version = $versions[$candidate.product]; file = 'packages/' + $file.Name; size = $file.Length; sha256 = $hash }
    }
    $manifest = [ordered]@{ schemaVersion = 1; id = [Guid]::NewGuid().ToString('D'); productionSourceCommit = $sourceCommit; packages = @($packages) }
    $manifestPath = Join-Path $output 'acceptance.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $launcherDirectory = Join-Path $output 'launcher'
    New-Item -ItemType Directory -Path $launcherDirectory | Out-Null
    foreach ($file in $frozenPayload) {
        $destination = Join-Path $launcherDirectory $file.name
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $frozen $file.name) -Destination $destination
    }
    dotnet publish tests/ToolkitLauncher.Acceptance/ToolkitLauncher.Acceptance.csproj -c Release -r win-x64 --self-contained true "-p:SourceRevisionId=$sourceCommit" "-p:FrozenToolkitDirectory=$frozen" -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $launcherDirectory *> (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw ('QA publish failed. See ' + (Join-Path $output 'build.log')) }
    foreach ($file in $frozenPayload) {
        if ((Get-FileHash -LiteralPath (Join-Path $launcherDirectory $file.name) -Algorithm SHA256).Hash -ne $file.sha256) {
            throw ('QA publish changed frozen production/runtime payload: ' + $file.name)
        }
    }
    $frozenPayload | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'frozen-payload.json') -Encoding utf8
    # ProjectReference copies the ordinary application's host too. Keep its DLL/resources,
    # but exclude alternate launch entrypoints so this bundle opens only the guarded host.
    foreach ($name in @('Production Toolkit.exe', 'Production Toolkit.deps.json', 'Production Toolkit.runtimeconfig.json')) {
        $extraHost = Join-Path (Join-Path $output 'launcher') $name
        if (Test-Path -LiteralPath $extraHost) { Remove-Item -LiteralPath $extraHost }
    }
    $launcher = Join-Path $output 'launcher/Toolkit.QA.exe'
    & $launcher --self-test *> (Join-Path $output 'self-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'Published QA infrastructure checks failed.' }
    & $launcher --check-wpf-startup (Join-Path $output 'wpf-startup') *> (Join-Path $output 'wpf-startup.log')
    if ($LASTEXITCODE -ne 0) { throw 'Published QA WPF startup/resource check failed.' }
    & $launcher --validate-manifest $manifestPath *> (Join-Path $output 'manifest-validation.log')
    if ($LASTEXITCODE -ne 0) { throw 'Published QA host rejected the exact candidate packages.' }
    if ([Environment]::MachineName -eq 'JOHN-PC') {
        & $launcher --manifest $manifestPath --disposable-vm JOHN-PC --allow-real-os-operations *> (Join-Path $output 'host-refusal.log')
        if ($LASTEXITCODE -ne 2 -or (Get-Content -LiteralPath (Join-Path $output 'host-refusal.log') -Raw) -notmatch 'forbidden on JOHN-PC') { throw 'Actual production-host refusal failed.' }
    }
    Copy-Item -LiteralPath (Join-Path $repo 'tests/ToolkitLauncher.Acceptance/README.md') -Destination $output
    Copy-Item -LiteralPath (Join-Path $repo 'tests/ToolkitLauncher.Acceptance/Launch-QA.ps1') -Destination $output
    [ordered]@{
        harnessSourceCommit = $harnessCommit; productionSourceCommit = $sourceCommit; productionFilesChanged = $false
        publishedPublicly = $false; productionBinaryAcceptance = $false; vmAcceptance = 'pending master-controlled run'
        productionAssembliesReused = $true; frozenToolkitDirectory = $frozen; frozenPayloadFilesVerified = $frozenPayload.Count
        productionExecutableSHA256 = $productionExecutableSHA256; productionExtractedWithoutRebuild = $true
        builtAtUtc = [DateTime]::UtcNow.ToString('o'); manifestId = $manifest.id
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'build-info.json') -Encoding utf8
    $hashes = Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName | ForEach-Object {
        ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) + '  ' + [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\', '/')
    }
    [IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), $hashes)
    Compress-Archive -LiteralPath $output -DestinationPath ($output + '.zip') -CompressionLevel Fastest
    Get-Item -LiteralPath ($output + '.zip') | Select-Object FullName,Length
    Get-FileHash -LiteralPath ($output + '.zip') -Algorithm SHA256
    # The deliberate JOHN-PC rejection returns 2. Do not leak that expected
    # native exit code as the result of a successfully validated bundle build.
    $global:LASTEXITCODE = 0
}
finally { Pop-Location }
