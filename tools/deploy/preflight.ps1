# Read-only server checks. Never prints tunnel command lines, credentials, or instance files.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Write-Host 'DocumentStorage server preflight'
Write-Host '--- .NET SDKs ---'
if (Get-Command dotnet -ErrorAction SilentlyContinue) { & dotnet --list-sdks } else { Write-Host 'dotnet: missing' }
Write-Host '--- Git ---'
if (Get-Command git -ErrorAction SilentlyContinue) { & git --version } else { Write-Host 'git: missing' }
Write-Host '--- Services (no command lines or tokens) ---'
foreach ($name in @('cloudflared', 'DocumentStorage')) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($service) { $service | Select-Object Name, Status, StartType | Format-Table -AutoSize }
    else { Write-Host "$name service: not installed" }
}
Write-Host '--- PostgreSQL tools ---'
foreach ($name in @('psql.exe', 'pg_dump.exe', 'pg_restore.exe')) {
    $path = Join-Path 'C:\Program Files\PostgreSQL\18\bin' $name
    Write-Host ($name + ': ' + (Test-Path $path))
}
Write-Host '--- PDF tool ---'
$pdf = Get-Command pdftotext.exe -ErrorAction SilentlyContinue
if ($pdf) { Write-Host $pdf.Source }
else { Write-Host ('Default Poppler location exists: ' + (Test-Path 'C:\Tools\poppler\Library\bin\pdftotext.exe')) }
foreach ($name in @('pdfinfo.exe', 'pdftoppm.exe')) { $tool = Get-Command $name -ErrorAction SilentlyContinue; Write-Host ($name + ': ' + [bool]$tool) }
Write-Host '--- Image OCR ---'
$ocr = Join-Path $env:ProgramFiles 'Tesseract-OCR\tesseract.exe'
if (Test-Path -LiteralPath $ocr) { & $ocr --version; & $ocr --list-langs } else { Write-Host 'Tesseract not installed in its default machine location.' }
Write-Host '--- Proposed origin port ---'
$listeners = Get-NetTCPConnection -LocalPort 5240 -State Listen -ErrorAction SilentlyContinue
if ($listeners) { $listeners | Select-Object LocalAddress, LocalPort | Format-Table -AutoSize }
else { Write-Host 'Port 5240 has no listening service.' }
