# Run after installing Tesseract. Checks service-readable tools without dumping private configuration.
[CmdletBinding()]
param([string]$InstallRoot = 'C:\Apps\DocumentStorage', [string]$TesseractCommand = 'C:\Program Files\Tesseract-OCR\tesseract.exe', [string]$Language = 'eng')
. (Join-Path $PSScriptRoot 'common.ps1')
Assert-Administrator
if ($Language -notmatch '^[a-zA-Z0-9_]+(\+[a-zA-Z0-9_]+)*$') { throw 'Use installed language codes, for example eng or eng+spa.' }
if (-not (Test-Path -LiteralPath $TesseractCommand -PathType Leaf)) { throw 'Install Tesseract first: winget install --id UB-Mannheim.TesseractOCR --exact --scope machine' }
$TesseractCommand = (Resolve-Path -LiteralPath $TesseractCommand).Path
Invoke-Native $TesseractCommand @('--version')
$languages = & $TesseractCommand --list-langs
if ($LASTEXITCODE -ne 0) { throw 'Tesseract could not list language data.' }
foreach ($code in $Language.Split('+')) { if ($languages -notcontains $code) { throw "Install the Tesseract language data for $code before configuring OCR." } }
# Use the dedicated installed tool folder, never a storage root or a Windows/system directory.
$toolDirectory = Split-Path -Parent $TesseractCommand
if ((Split-Path -Leaf $toolDirectory) -ine 'Tesseract-OCR') { throw 'Install Tesseract into its dedicated Tesseract-OCR directory before granting service access.' }
Invoke-Native icacls @($toolDirectory, '/grant', '*S-1-5-19:(OI)(CI)(RX)', '/T', '/Q')
$configPath = Join-Path $InstallRoot 'instance.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if (-not $config.PSObject.Properties['Documents']) { $config | Add-Member -MemberType NoteProperty -Name Documents -Value ([pscustomobject]@{}) }
# Poppler's existing text-tool directory also supplies page metadata and rendering.
if (-not $config.Documents.PSObject.Properties['PdfTextCommand']) { throw 'Configure the installed Poppler pdftotext.exe path before setting up PDF OCR.' }
$pdfText = [string]$config.Documents.PdfTextCommand
if (-not (Test-Path -LiteralPath $pdfText -PathType Leaf)) { throw 'The configured Poppler text tool was not found. Correct Documents.PdfTextCommand first.' }
$pdfText = (Resolve-Path -LiteralPath $pdfText).Path
$pdfDirectory = Split-Path -Parent $pdfText
if ((Split-Path -Leaf $pdfDirectory) -notmatch '^(bin|poppler([-.].*)?)$') { throw 'Use the dedicated Poppler bin directory before granting service access.' }
$pdfInfo = Join-Path $pdfDirectory 'pdfinfo.exe'
$pdfRaster = Join-Path $pdfDirectory 'pdftoppm.exe'
if ($config.Documents.PSObject.Properties['PdfInfoCommand']) { $pdfInfo = [string]$config.Documents.PdfInfoCommand }
if ($config.Documents.PSObject.Properties['PdfRasterCommand']) { $pdfRaster = [string]$config.Documents.PdfRasterCommand }
foreach ($pdfTool in @($pdfText, $pdfInfo, $pdfRaster)) {
    if (-not (Test-Path -LiteralPath $pdfTool -PathType Leaf)) { throw 'PDF OCR needs the full Poppler package: pdftotext.exe, pdfinfo.exe and pdftoppm.exe.' }
    if ((Split-Path -Parent ([IO.Path]::GetFullPath($pdfTool))) -ine $pdfDirectory) { throw 'Keep the PDF OCR tools in the same dedicated Poppler bin directory for service setup.' }
    Invoke-Native $pdfTool @('-v')
}
Invoke-Native icacls @($pdfDirectory, '/grant', '*S-1-5-19:(OI)(CI)(RX)', '/T', '/Q')
$config.Documents | Add-Member -MemberType NoteProperty -Name PdfTextCommand -Value $pdfText -Force
$config.Documents | Add-Member -MemberType NoteProperty -Name PdfInfoCommand -Value $pdfInfo -Force
$config.Documents | Add-Member -MemberType NoteProperty -Name PdfRasterCommand -Value $pdfRaster -Force
$config.Documents | Add-Member -MemberType NoteProperty -Name TesseractCommand -Value $TesseractCommand -Force
$config.Documents | Add-Member -MemberType NoteProperty -Name OcrLanguage -Value $Language -Force
# Preserve the protected file's ACL, keep all existing settings and avoid displaying its contents.
$config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath -Encoding UTF8
Restart-Service DocumentStorage
Write-Host 'OCR configured. In Stash, open Settings > File formats > Check OCR engine and Check PDF tools. Waiting jobs retry automatically.'
