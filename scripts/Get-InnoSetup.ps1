$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compilerRoot = Join-Path $taskRoot 'artifacts\tools\InnoSetup-6.7.3'
$compilerPath = Join-Path $compilerRoot 'ISCC.exe'
if (Test-Path -LiteralPath $compilerPath) { return $compilerPath }
$installer = Join-Path $taskRoot 'artifacts\tools\innosetup-6.7.3.exe'
New-Item -ItemType Directory -Force -Path (Split-Path $installer) | Out-Null
Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $installer
$expectedHash = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Inno Setup compiler download checksum mismatch.' }
$setupProcess = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', ('/DIR="' + $compilerRoot + '"'), '/NOICONS', '/TASKS=') -WindowStyle Hidden -Wait -PassThru
if ($setupProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compilerPath)) { throw 'Inno Setup compiler installation failed.' }
return $compilerPath
