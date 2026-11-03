# Update a clean, already installed instance. Builds and backs up before stopping the service.
[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\Apps\DocumentStorage',
    [string]$PostgresBin = 'C:\Program Files\PostgreSQL\18\bin'
)
. (Join-Path $PSScriptRoot 'common.ps1')
Assert-Administrator
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ($repository -ne (Join-Path $InstallRoot 'repo')) { throw 'Run the script from this instance repo directory.' }
$config = Get-Content -LiteralPath (Join-Path $InstallRoot 'instance.json') -Raw | ConvertFrom-Json
if ($config.Hosting.PublicOrigin -notmatch '^https://' -or -not $config.CloudflareAccess.Audience) { throw 'Authenticated public hosting must be configured before updating the server instance.' }
$revision = Get-Revision $repository
$release = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$staging = Join-Path $InstallRoot "staging\$release"
$previous = Join-Path $InstallRoot "releases\$release"
$publish = Join-Path $InstallRoot 'publish'
$service = Get-CimInstance Win32_Service -Filter "Name='DocumentStorage'"
if ($null -eq $service -or $service.PathName -notlike ('*"' + (Join-Path $publish 'DocumentStorage.Web.dll') + '"*')) { throw 'The DocumentStorage service does not point to this instance. Nothing will be stopped.' }
if (-not (Test-Path (Join-Path $PostgresBin 'pg_dump.exe')) -or -not (Test-Path (Join-Path $PostgresBin 'pg_restore.exe'))) { throw 'PostgreSQL backup and restore tools are required before an update.' }
Publish-Release $repository $staging $revision

Protect-Directory (Join-Path $InstallRoot 'backups')
$dump = Join-Path $InstallRoot "backups\catalog-$release.dump"
$connection = New-Object System.Data.Common.DbConnectionStringBuilder
$connection.ConnectionString = $config.ConnectionStrings.Catalog
$oldPgPassword = $env:PGPASSWORD
try {
    $env:PGPASSWORD = $config.DOCUMENTSTORAGE_DB_PASSWORD
    Invoke-Native (Join-Path $PostgresBin 'pg_dump.exe') @('--host', [string]$connection['Host'], '--port', [string]$connection['Port'], '--username', [string]$connection['Username'], '--dbname', [string]$connection['Database'], '--no-password', '--format=custom', '--file', $dump)
    Invoke-Native (Join-Path $PostgresBin 'pg_restore.exe') @('--list', $dump)
    if ((Get-Item -LiteralPath $dump).Length -eq 0) { throw 'The catalog backup is empty.' }
} finally { $env:PGPASSWORD = $oldPgPassword }

$swapped = $false
$oldRevision = $null
try { $oldRevision = (Invoke-RestMethod 'http://127.0.0.1:5240/health' -TimeoutSec 5).revision } catch { }
try {
    Stop-Service DocumentStorage
    (Get-Service DocumentStorage).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    New-Item -ItemType Directory -Path (Split-Path $previous) -Force | Out-Null
    Move-Item -LiteralPath $publish -Destination $previous
    try { Move-Item -LiteralPath $staging -Destination $publish }
    catch { Move-Item -LiteralPath $previous -Destination $publish; throw }
    $swapped = $true
    Start-Service DocumentStorage
    Wait-Healthy -Port 5240 -Revision $revision -ServiceName DocumentStorage
    Write-Host "Release $revision is healthy. Previous binaries retained at $previous. Catalog backup: $dump"
} catch {
    $failure = $_
    Stop-Service DocumentStorage -ErrorAction SilentlyContinue
    if ($swapped) {
        (Get-Service DocumentStorage).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        Move-Item -LiteralPath $publish -Destination (Join-Path $InstallRoot "releases\failed-$release")
        Move-Item -LiteralPath $previous -Destination $publish
    }
    Start-Service DocumentStorage -ErrorAction SilentlyContinue
    if ($oldRevision) {
        try { Wait-Healthy -Port 5240 -Revision $oldRevision -ServiceName DocumentStorage }
        catch { Write-Warning 'Previous binaries did not recover. Stop the service and inspect the logs and catalog backup.' }
    }
    Write-Warning 'Binary rollback does not reverse database migrations or restore document files. The catalog backup is retained; do not restore it blindly over newer changes.'
    throw $failure
}
