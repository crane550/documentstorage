# Run after installing LibreOffice. Preserves the protected instance file and all existing settings.
[CmdletBinding()]
param([string]$InstallRoot = 'C:\Apps\DocumentStorage', [string]$OfficeCommand = 'C:\Program Files\LibreOffice\program\soffice.com')
. (Join-Path $PSScriptRoot 'common.ps1')
Assert-Administrator
if (-not (Test-Path -LiteralPath $OfficeCommand -PathType Leaf)) { throw 'Install LibreOffice first: winget install --id TheDocumentFoundation.LibreOffice --exact --scope machine' }
$OfficeCommand = (Resolve-Path -LiteralPath $OfficeCommand).Path
$program = Split-Path -Parent $OfficeCommand
$toolDirectory = Split-Path -Parent $program
if ((Split-Path -Leaf $toolDirectory) -ine 'LibreOffice' -or (Split-Path -Leaf $program) -ine 'program' -or (Split-Path -Leaf $OfficeCommand) -ine 'soffice.com') { throw 'Use soffice.com inside the dedicated LibreOffice\program installation directory before granting service access.' }
Invoke-Native $OfficeCommand @('--version')
Invoke-Native icacls @($toolDirectory, '/grant', '*S-1-5-19:(OI)(CI)(RX)', '/T', '/Q')
$configPath = Join-Path $InstallRoot 'instance.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if (-not $config.PSObject.Properties['Documents']) { $config | Add-Member -MemberType NoteProperty -Name Documents -Value ([pscustomobject]@{}) }
$config.Documents | Add-Member -MemberType NoteProperty -Name OfficeCommand -Value $OfficeCommand -Force
$config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath -Encoding UTF8
Restart-Service DocumentStorage
Write-Host 'Office conversion configured. In Stash, open Settings > File formats > Check Office converter. Waiting jobs retry automatically.'
