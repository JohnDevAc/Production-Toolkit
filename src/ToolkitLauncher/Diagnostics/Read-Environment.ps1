# Read-only local evidence for the environment managed by Kiloview Environment Setup.
# Do not invoke the installer, change services, start tasks, or launch a WSL distribution.
$ErrorActionPreference = 'Stop'
$result = [ordered]@{
    Configuration = $null; Distro = $null; DistroName = 'KiloLink-Ubuntu'; OtherUser = $false
    Watchdog = $null; WatchdogRunning = $null; NdiTools = $null; NdiVersion = $null
    Discovery = $null; DiscoveryRunning = $null; DiscoveryListening = $null
    RestartPending = $false; WebPort = 0; NdiPort = 5959; Note = ''
    Roles = $null; ComponentReceiptInvalid = $false; PcAgent = $null; PcAgentConfigured = $null; PcAgentConfiguration = $null
}
$stateRoot = Join-Path $env:ProgramData 'KiloLink'
$configPath = Join-Path $stateRoot 'installer-config.json'
$notes = New-Object 'System.Collections.Generic.List[string]'
try {
    $receiptPath = Join-Path $stateRoot 'installation-components.json'
    if (Test-Path -LiteralPath $receiptPath) {
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        if ($receipt.schemaVersion -ne 1 -or $receipt.roles -isnot [array] -or @($receipt.roles | Where-Object { $_ -notin @('client','server') }).Count -gt 0) {
            throw 'Unsupported component receipt.'
        }
        $result.Roles = @($receipt.roles)
    }
} catch { $result.ComponentReceiptInvalid = $true; $notes.Add('The saved component receipt is unreadable or unsupported.') }
try {
    $agentRoot = Join-Path $env:ProgramFiles 'NDI Configurator\PC Agent'
    $result.PcAgent = (Test-Path -LiteralPath (Join-Path $agentRoot 'NDI Configurator PC Agent.exe')) -and (Test-Path -LiteralPath (Join-Path $agentRoot 'NDI Configurator PC Agent Setup.exe'))
    $agentState = Join-Path $env:LOCALAPPDATA 'NDI Configurator\PC Agent\agent-state.json'
    $result.PcAgentConfigured = $false
    if (Test-Path -LiteralPath $agentState) {
        $agentConfig = Get-Content -LiteralPath $agentState -Raw | ConvertFrom-Json
        $result.PcAgentConfiguration = [ordered]@{
            SchemaVersion = $agentConfig.schemaVersion; EndpointId = $agentConfig.endpointId
            AdapterId = $agentConfig.adapterId; Address = $agentConfig.address; PrefixLength = $agentConfig.prefixLength
        }
    }
} catch { $notes.Add('The selected component receipt or PC Agent configuration could not be verified.') }
try {
    $result.RestartPending = Test-Path -LiteralPath (Join-Path $stateRoot 'resume-state.json')
    if (Test-Path -LiteralPath $configPath) {
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $port = 0; $ndiPort = 0
        if ([string]$config.DistroName -notmatch '^[a-zA-Z0-9_.-]{1,64}$' -or
            -not [int]::TryParse([string]$config.WebPort, [ref]$port) -or $port -lt 1 -or $port -gt 65535 -or
            -not [int]::TryParse([string]$config.NdiDiscoveryPort, [ref]$ndiPort) -or $ndiPort -lt 1 -or $ndiPort -gt 65535) {
            throw 'Invalid environment configuration.'
        }
        $result.Configuration = $true
        $result.DistroName = [string]$config.DistroName
        $result.WebPort = $port; $result.NdiPort = $ndiPort
    } else { $result.Configuration = $false }
} catch { $notes.Add('Environment configuration could not be verified. Open Setup to review it.') }

$tasks = $null; $ndiTask = $null; $tasksRead = $false
try {
    $tasks = @(Get-ScheduledTask -TaskPath '\')
    $tasksRead = $true
    $startup = @($tasks | Where-Object TaskName -eq 'KiloLink WSL Startup') | Select-Object -First 1
    $ndiTask = @($tasks | Where-Object TaskName -eq 'NDI Discovery Server Startup') | Select-Object -First 1
    $result.Watchdog = [bool]($startup -and (Test-Path -LiteralPath (Join-Path $stateRoot 'start-kilolink.ps1')))
    $result.WatchdogRunning = [bool]($startup -and $startup.State -eq 'Running')
    if ($startup -and $startup.Principal.UserId) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $owner = [string]$startup.Principal.UserId
        if ($owner -notmatch '^S-1-') {
            try {
                $account = New-Object Security.Principal.NTAccount($owner)
                $owner = $account.Translate([Security.Principal.SecurityIdentifier]).Value
            } catch {
                $result.OtherUser = $true
                throw 'The startup task account could not be resolved.'
            }
        }
        $result.OtherUser = $owner -ne $identity.User.Value
        if ($result.OtherUser) { $notes.Add('KiloLink belongs to another Windows account; its WSL installation cannot be verified here.') }
    }
} catch { $notes.Add('Startup tasks could not be read with the current Windows permissions.') }

if (-not $result.OtherUser) {
    try {
        $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss'
        $result.Distro = $false
        if (Test-Path -LiteralPath $key) {
            foreach ($child in Get-ChildItem -LiteralPath $key) {
                $entry = Get-ItemProperty -LiteralPath $child.PSPath
                if ($entry.DistributionName -eq $result.DistroName) { $result.Distro = $true }
            }
        }
    } catch { $result.Distro = $null; $notes.Add('The managed WSL registration could not be read.') }
}

$ndiExe = $null
try {
    $registered = foreach ($path in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        if (Test-Path -LiteralPath $path) {
            foreach ($key in Get-ChildItem -LiteralPath $path) {
                $item = Get-ItemProperty -LiteralPath $key.PSPath
                if ($item.DisplayName -match '^NDI\s+\d+\s+Tools' -or
                    ($item.DisplayName -match 'NDI.*Tools' -and $item.Publisher -match 'NDI|Vizrt|NewTek')) { $item }
            }
        }
    }
    $registration = @($registered) | Select-Object -First 1
    $result.NdiTools = [bool]$registration
    if ($registration) { $result.NdiVersion = [string]$registration.DisplayVersion }
    $roots = @((Join-Path $env:ProgramFiles 'NDI'))
    if ($registration.InstallLocation) { $roots += [string]$registration.InstallLocation }
    foreach ($root in $roots | Select-Object -Unique) {
        if (Test-Path -LiteralPath $root) {
            $ndiExe = Get-ChildItem -LiteralPath $root -Filter 'NDI Discovery Service.exe' -File -Recurse | Select-Object -First 1
            if ($ndiExe) { break }
        }
    }
    # Registration alone can survive a damaged or manually removed installation.
    if ($result.NdiTools) {
        $ndiLauncher = $null
        foreach ($root in $roots | Select-Object -Unique) {
            if (Test-Path -LiteralPath $root) {
                $ndiLauncher = Get-ChildItem -LiteralPath $root -Filter 'NDI Launcher.exe' -File -Recurse | Select-Object -First 1
                if ($ndiLauncher) { break }
            }
        }
        $result.NdiTools = [bool]$ndiLauncher
        if ($ndiLauncher -and $ndiLauncher.VersionInfo.ProductVersion) { $result.NdiVersion = $ndiLauncher.VersionInfo.ProductVersion }
    }
    $service = Get-CimInstance Win32_Service -Filter "Name LIKE '%NDI%Discovery%' OR DisplayName LIKE '%NDI%Discovery%'" | Select-Object -First 1
    $result.Discovery = if ($ndiExe -and ($service -or $ndiTask)) { $true }
        elseif (-not $ndiExe -or $tasksRead) { $false } else { $null }
    $processIds = @()
    if ($service) {
        $result.DiscoveryRunning = $service.State -eq 'Running'
        if ($service.ProcessId -gt 0) { $processIds += [int]$service.ProcessId }
    } elseif ($tasksRead) {
        $result.DiscoveryRunning = [bool]($ndiTask -and $ndiTask.State -eq 'Running')
        if ($result.DiscoveryRunning -and $ndiExe) {
            $processIds = @(Get-Process -Name 'NDI Discovery Service' -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -eq $ndiExe.FullName } | Select-Object -ExpandProperty Id)
        }
    }
    if ($result.DiscoveryRunning -eq $true -and $processIds.Count -gt 0) {
        $listeners = @(Get-NetTCPConnection -State Listen | Where-Object {
            $_.LocalPort -eq $result.NdiPort -and $_.OwningProcess -in $processIds -and $_.LocalAddress -in @('0.0.0.0', '::')
        })
        $result.DiscoveryListening = $listeners.Count -gt 0
    } elseif ($result.DiscoveryRunning -eq $false) { $result.DiscoveryListening = $false }
} catch { $notes.Add('Some NDI installation or service details could not be verified.') }
$result.Note = $notes -join ' '
$result | ConvertTo-Json -Compress
