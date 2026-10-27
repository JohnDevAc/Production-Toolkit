param([Parameter(Mandatory)][string]$ComputerName)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'launcher/Toolkit.QA.exe') --manifest (Join-Path $PSScriptRoot 'acceptance.json') --disposable-vm $ComputerName --allow-real-os-operations
if ($LASTEXITCODE -ne 0) { throw "QA launcher exited with code $LASTEXITCODE. See the console and QA evidence directory." }
