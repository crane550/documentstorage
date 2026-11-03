# First install only. Re-running never overwrites an existing instance or service.
[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\Apps\DocumentStorage',
    [Parameter(Mandatory)][string]$AccessTeamDomain,
    [Parameter(Mandatory)][string]$AccessAudience,
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
if ($AccessTeamDomain -notmatch '^[a-z0-9-]+\.cloudflareaccess\.com$' -or [string]::IsNullOrWhiteSpace($AccessAudience)) { throw 'Supply the Access team domain and this application AUD tag from Cloudflare.' }
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
    $config = [ordered]@{
        Application = @{ DisplayName = 'Document Manager' }
        Urls = 'http://127.0.0.1:5240'
        Hosting = @{ ServiceName = 'DocumentStorage'; PublicOrigin = $PublicOrigin; DataProtectionDirectory = (Join-Path $InstallRoot 'keys') }
        CloudflareAccess = @{ TeamDomain = $AccessTeamDomain; Audience = $AccessAudience }
        ConnectionStrings = @{ Catalog = 'Host=127.0.0.1;Port=5432;Database=documentstorage;Username=documentstorage' }
        DOCUMENTSTORAGE_DB_PASSWORD = $env:PGPASSWORD
        Documents = @{ PdfTextCommand = $PdfTextCommand }
    }
    $config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding UTF8
} finally { $env:PGPASSWORD = $oldPgPassword; $credential = $null; $config = $null }

$binary = '"' + $dotnet + '" "' + (Join-Path $publish 'DocumentStorage.Web.dll') + '" --environment Production'
if (-not [Diagnostics.EventLog]::SourceExists('DocumentStorage')) {
    New-EventLog -LogName Application -Source DocumentStorage
}
$serviceCredential = [System.Management.Automation.PSCredential]::new('NT AUTHORITY\LocalService', [System.Security.SecureString]::new())
New-Service -Name DocumentStorage -DisplayName 'Document Manager' -BinaryPathName $binary -StartupType Automatic -Credential $serviceCredential | Out-Null
Start-Service DocumentStorage
Wait-Healthy -Port 5240 -Revision $revision -ServiceName DocumentStorage
Write-Host "DocumentStorage release $revision is healthy on loopback port 5240."
Write-Host 'The service runs as LocalService. Add folder permissions only for the storage you intend to manage.'
Write-Host 'Configure and test Cloudflare Access before adding the tunnel route. See tools/deploy/README.md.'
