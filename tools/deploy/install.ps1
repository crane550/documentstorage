# First install only. Re-running never overwrites an existing instance or service.
[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\Apps\DocumentStorage',
    [string]$PublicOrigin = 'https://stash.instrumentsofliberty.com',
    [string]$PostgresBin = 'C:\Program Files\PostgreSQL\18\bin',
    [Parameter(Mandatory)][string]$PdfTextCommand
)
. (Join-Path $PSScriptRoot 'common.ps1')
Assert-Administrator
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ($repository -ne (Join-Path $InstallRoot 'repo')) { throw 'Clone into <InstallRoot>\repo and run its install script.' }
$configPath = Join-Path $InstallRoot 'instance.json'
if ((Test-Path $configPath) -or (Get-Service DocumentStorage -ErrorAction SilentlyContinue)) { throw 'An instance or service already exists. Use deploy.ps1 for updates.' }
if (-not (Test-Path (Join-Path $PostgresBin 'psql.exe'))) { throw 'PostgreSQL 18 psql.exe is required.' }
if (-not (Test-Path $PdfTextCommand)) { throw 'Supply the installed Poppler pdftotext.exe path.' }
$origin = [Uri]$PublicOrigin
if ($origin.Scheme -ne 'https' -or $origin.AbsolutePath -ne '/' -or $origin.Query -or $origin.Fragment -or $origin.UserInfo -or $origin.Port -ne 443) { throw 'PublicOrigin must be an HTTPS origin with no path.' }
$revision = Get-Revision $repository
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$publish = Join-Path $InstallRoot 'publish'
if (Test-Path $publish) { throw 'The publish directory already exists. Preserve it before a first install.' }
Publish-Release $repository $publish $revision

# Restrict the instance before creating any secret-bearing file. LocalService can read binaries/config;
# only keys and the initial storage directory are writable by the service.
Protect-Directory $InstallRoot
Protect-Directory (Join-Path $InstallRoot 'keys') -ServiceCanModify
Protect-Directory (Join-Path $InstallRoot 'storage') -ServiceCanModify
$credential = Get-Credential -UserName documentstorage -Message 'Password for the NEW documentstorage PostgreSQL role (not your Windows account)'
if ($null -eq $credential) { throw 'No database credential supplied; nothing was started.' }
$oldPgPassword = $env:PGPASSWORD
try {
    $env:PGPASSWORD = $credential.GetNetworkCredential().Password
    Invoke-Native (Join-Path $PostgresBin 'psql.exe') @('-h','127.0.0.1','-p','5432','-U','documentstorage','-d','documentstorage','-w','-v','ON_ERROR_STOP=1','-c','SELECT current_database(), current_user;')
    $adminCredential = Get-Credential -UserName admin -Message 'Choose the FIRST document workspace administrator username and password (12 characters minimum)'
    if ($null -eq $adminCredential -or $adminCredential.GetNetworkCredential().Password.Length -lt 12) { throw 'Supply an administrator password with at least 12 characters.' }
    $config = [ordered]@{
        Application = @{ DisplayName = 'Document Manager' }
        Urls = 'http://127.0.0.1:5240'
        Hosting = @{ ServiceName = 'DocumentStorage'; PublicOrigin = $PublicOrigin; DataProtectionDirectory = (Join-Path $InstallRoot 'keys') }
        BootstrapAdmin = @{ Username = $adminCredential.UserName; Password = $adminCredential.GetNetworkCredential().Password }
        ConnectionStrings = @{ Catalog = 'Host=127.0.0.1;Port=5432;Database=documentstorage;Username=documentstorage' }
        DOCUMENTSTORAGE_DB_PASSWORD = $env:PGPASSWORD
        Documents = @{ PdfTextCommand = $PdfTextCommand }
    }
    $config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding UTF8
} finally { $env:PGPASSWORD = $oldPgPassword; $credential = $null; $adminCredential = $null; $config = $null }

$binary = '"' + $dotnet + '" "' + (Join-Path $publish 'DocumentStorage.Web.dll') + '" --environment Production'
if (-not [Diagnostics.EventLog]::SourceExists('DocumentStorage')) {
    New-EventLog -LogName Application -Source DocumentStorage
}
$serviceCredential = [System.Management.Automation.PSCredential]::new('NT AUTHORITY\LocalService', [System.Security.SecureString]::new())
New-Service -Name DocumentStorage -DisplayName 'Document Manager' -BinaryPathName $binary -StartupType Automatic -Credential $serviceCredential | Out-Null
Start-Service DocumentStorage
Wait-Healthy -Port 5240 -Revision $revision -ServiceName DocumentStorage
# The password is now hashed in PostgreSQL. Remove its one-time bootstrap copy.
$privateConfig = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$privateConfig.BootstrapAdmin.PSObject.Properties.Remove('Password')
$privateConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding UTF8
$privateConfig = $null
Write-Host "DocumentStorage release $revision is healthy on loopback port 5240."
Write-Host 'The service runs as LocalService. Add folder permissions only for the storage you intend to manage.'
Write-Host 'Route the Cloudflare tunnel to http://127.0.0.1:5240 and use the app login. No Access audience is needed. See tools/deploy/README.md.'
