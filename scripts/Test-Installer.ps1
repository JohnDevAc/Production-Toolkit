$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testId = 'ProductionToolkit.Test.' + [Guid]::NewGuid().ToString('N')
$testTitle = 'Production Toolkit Installer Test ' + $testId.Substring($testId.Length - 8)
$testRoot = Join-Path $taskRoot ('artifacts\installer-test\' + $testId)
$installRoot = Join-Path $testRoot 'app'
$baselineRoot = Join-Path $testRoot 'baseline'
$testPackages = Join-Path $testRoot 'packages'
$testExe = Join-Path $installRoot 'Production Toolkit.exe'
$registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $testId + '_is1'
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ($testTitle + '.lnk')
$startLink = Join-Path ([Environment]::GetFolderPath('Programs')) ($testTitle + '.lnk')
$version = ([xml](Get-Content -LiteralPath (Join-Path $taskRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$compiler = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1')
$checks = 0
$launchParameters = '--skip-startup-checks --isolated-test-state'

function Assert-Test($condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:checks++
    Write-Host ('PASS ' + $message)
}
function Run-Setup([string]$path, [string[]]$setupArguments) {
    $process = Start-Process -FilePath $path -ArgumentList $setupArguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { throw ('Installer timed out. Review logs in ' + $testRoot) }
    $process.Refresh()
    return $process.ExitCode
}
function Find-TestApp {
    return @(Get-Process -Name 'Production Toolkit' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $testExe })
}
function Compile-TestSetup([string]$buildVersion, [string]$payload) {
    & $compiler /Qp ('/DLaunchParameters=' + $launchParameters) ('/DAppVersion=' + $buildVersion) ('/DAppIdentity=' + $testId) ('/DAppTitle=' + $testTitle) ('/DPublishDirectory=' + $payload) ('/DReleaseDirectory=' + $testPackages) (Join-Path $taskRoot 'installer\ProductionToolkit.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Test installer compilation failed.' }
}

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
Push-Location $taskRoot
try {
    # A prior version with the same maintenance protocol exercises real binary replacement.
    dotnet publish src\ToolkitLauncher\ToolkitLauncher.csproj -c Release -r win-x64 --self-contained true -p:Version=1.1.9 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $baselineRoot
    if ($LASTEXITCODE -ne 0) { throw 'Baseline test application build failed.' }
    Compile-TestSetup '1.1.9' $baselineRoot
    Compile-TestSetup $version (Join-Path $taskRoot 'artifacts\publish\win-x64')
    $baselineSetup = Join-Path $testPackages 'Production-Toolkit-1.1.9-win-x64-Setup.exe'
    $newSetup = Join-Path $testPackages "Production-Toolkit-$version-win-x64-Setup.exe"
    $common = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-')
    $code = Run-Setup $baselineSetup ($common + @(('/DIR="' + $installRoot + '"'), ('/LOG="' + (Join-Path $testRoot 'install.log') + '"')))
    Assert-Test ($code -eq 0 -and (Test-Path -LiteralPath $testExe)) 'Initial per-user installation succeeds'
    Assert-Test ((Get-ItemProperty -LiteralPath $registryPath).DisplayVersion -eq '1.1.9') 'Windows Installed apps contains the correct version'
    Assert-Test ((Test-Path -LiteralPath $desktopLink) -and (Test-Path -LiteralPath $startLink)) 'Desktop and Start menu shortcuts are created'
    $shell = New-Object -ComObject WScript.Shell
    Assert-Test (($shell.CreateShortcut($desktopLink).TargetPath -eq $testExe) -and ($shell.CreateShortcut($startLink).TargetPath -eq $testExe)) 'Both shortcuts point to the installed executable'
    Assert-Test (($shell.CreateShortcut($desktopLink).Arguments -eq $launchParameters) -and ($shell.CreateShortcut($startLink).Arguments -eq $launchParameters)) 'Fixture shortcuts retain isolated startup parameters'
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    $marker = Join-Path $installRoot 'user-preserved-marker.txt'
    Set-Content -LiteralPath $marker -Value 'Keep on upgrade and uninstall'
    $running = Start-Process -FilePath $testExe -ArgumentList $launchParameters -WindowStyle Hidden -PassThru
    Assert-Test (-not $running.WaitForExit(4000)) 'Installed application launches'
    Assert-Test (Test-Path -LiteralPath (Join-Path $installRoot 'qa-state')) 'Installed fixture uses its own preferences directory'
    $duplicate = Start-Process -FilePath $testExe -ArgumentList $launchParameters -WindowStyle Hidden -PassThru
    Assert-Test ($duplicate.WaitForExit(10000)) 'Launching a shortcut again reuses the existing instance'
    $code = Run-Setup $newSetup ($common + @('/TOOLKITUPDATE=1', ('/LOG="' + (Join-Path $testRoot 'upgrade.log') + '"')))
    Assert-Test ($code -eq 0 -and $running.WaitForExit(10000)) 'Upgrade gracefully closes the running old version'
    Assert-Test ((Get-Item -LiteralPath $testExe).VersionInfo.FileVersion -eq ($version + '.0')) 'Upgrade replaces the existing executable with the new version'
    Assert-Test ((Get-ItemProperty -LiteralPath $registryPath).DisplayVersion -eq $version) 'Upgrade updates the same Installed apps entry'
    Assert-Test ((Get-ItemProperty -LiteralPath $registryPath).InstallLocation.TrimEnd('\') -eq $installRoot) 'Upgrade retains the original installation folder'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do { $restarted = Find-TestApp; if ($restarted.Count -gt 0) { break }; Start-Sleep -Milliseconds 250 } while ([DateTime]::UtcNow -lt $deadline)
    Assert-Test ($restarted.Count -eq 1) 'Upgrade automatically relaunches one copy of the installed app'
    Assert-Test (Test-Path -LiteralPath $marker) 'Upgrade preserves existing user files'
    $previousProcess = $restarted[0]
    $code = Run-Setup $newSetup ($common + @(('/LOG="' + (Join-Path $testRoot 'manual-upgrade.log') + '"')))
    Assert-Test ($code -eq 0 -and $previousProcess.WaitForExit(10000)) 'A manually run installer also closes the running app'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do { $restarted = Find-TestApp; if ($restarted.Count -gt 0) { break }; Start-Sleep -Milliseconds 250 } while ([DateTime]::UtcNow -lt $deadline)
    Assert-Test ($restarted.Count -eq 1) 'A manually run installer relaunches the app exactly once'
    $code = Run-Setup $baselineSetup ($common + @(('/LOG="' + (Join-Path $testRoot 'downgrade.log') + '"')))
    Assert-Test ($code -ne 0 -and (Get-Item -LiteralPath $testExe).VersionInfo.FileVersion -eq ($version + '.0')) 'An older installer cannot overwrite a newer app'
    $code = Run-Setup (Join-Path $installRoot 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $testRoot 'uninstall.log') + '"'))
    Assert-Test ($code -eq 0 -and -not (Test-Path -LiteralPath $testExe)) 'Uninstall closes the running app and removes its executable'
    Assert-Test (-not (Test-Path -LiteralPath $registryPath)) 'Uninstall removes the Windows Installed apps entry'
    Assert-Test (-not (Test-Path -LiteralPath $desktopLink) -and -not (Test-Path -LiteralPath $startLink)) 'Uninstall removes both shortcuts'
    Assert-Test (Test-Path -LiteralPath $marker) 'Uninstall leaves user-created files intact'
    Write-Host "PASS: $checks installer lifecycle checks. Logs: $testRoot"
}
finally {
    # Only this uniquely named test installation is touched. Never delete arbitrary app folders.
    $uninstaller = Join-Path $installRoot 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        Run-Setup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') | Out-Null
    }
    Pop-Location
}
