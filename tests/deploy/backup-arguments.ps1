# Run with Windows PowerShell 5.1 or PowerShell 7; synthetic settings only.
. (Join-Path $PSScriptRoot '../../tools/deploy/common.ps1')

$backup = 'synthetic backups/catalog.dump'
$actual = @(Get-CatalogBackupArguments -ConnectionString 'Host=127.0.0.1;Port=55432;Database=synthetic;Username=synthetic' -BackupPath $backup)
$expected = @('--host', '127.0.0.1', '--port', '55432', '--username', 'synthetic', '--dbname', 'synthetic', '--no-password', '--format=custom', '--file', $backup)
if (($actual | ConvertTo-Json -Compress) -ne ($expected | ConvertTo-Json -Compress)) { throw 'PostgreSQL backup arguments do not match the configured destination.' }
Write-Host 'PASS backup receives parsed PostgreSQL host, port, username, database and output path'

$actual = @(Get-CatalogBackupArguments -ConnectionString 'hOsT=localhost;PORT=5432;Database="synthetic;archive";Username="synthetic user";Password="fixture;secret"' -BackupPath $backup)
if ($actual[1] -ne 'localhost' -or $actual[7] -ne 'synthetic;archive' -or $actual[5] -ne 'synthetic user' -or $actual -contains 'fixture;secret') { throw 'Quoted settings, case or credential separation failed.' }
Write-Host 'PASS quoted values and case survive parsing without placing passwords in arguments'

foreach ($missing in @('Host', 'Port', 'Username', 'Database')) {
    $settings = @{ Host='127.0.0.1'; Port='5432'; Username='synthetic'; Database='synthetic' }
    $settings.Remove($missing)
    $text = ($settings.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ';'
    $refused = $false
    try { Get-CatalogBackupArguments -ConnectionString $text -BackupPath $backup | Out-Null }
    catch { if ($_.Exception.Message -notlike "*must include $missing*") { throw }; $refused = $true }
    if (-not $refused) { throw "Missing $missing was accepted." }
    Write-Host "PASS missing $missing is refused before invoking backup tools or stopping the service"
}
Write-Host 'All 6 deployment backup argument checks passed.'
