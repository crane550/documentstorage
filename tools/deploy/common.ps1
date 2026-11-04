Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script in an elevated PowerShell window on the server.'
    }
}

function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE." }
}

function Protect-Directory {
    param([string]$Path, [switch]$ServiceCanModify)
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($sid in @('S-1-5-32-544', 'S-1-5-18', 'S-1-5-19')) {
        $identity = New-Object Security.Principal.SecurityIdentifier($sid)
        $rights = if ($sid -eq 'S-1-5-19') {
            if ($ServiceCanModify) { 'Modify' } else { 'ReadAndExecute' }
        } else { 'FullControl' }
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity, $rights, $inheritance, 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Get-Revision {
    param([string]$Repository)
    $changes = & git -C $Repository status --porcelain
    if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Deploy only a clean checkout. Preserve local changes before updating.' }
    $revision = & git -C $Repository rev-parse --verify 'HEAD^{commit}'
    if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify the commit to deploy.' }
    return $revision
}

function Wait-Healthy {
    param([int]$Port, [string]$Revision, [string]$ServiceName)
    $deadline = (Get-Date).AddSeconds(60)
    do {
        try {
            $service = Get-Service -Name $ServiceName
            $reply = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 3
            if ($service.Status -eq 'Running' -and $reply.status -eq 'ready' -and $reply.revision -eq $Revision) { return }
        } catch { }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    throw 'The expected release did not become healthy. Inspect the Windows Application event log.'
}

function Publish-Release {
    param([string]$Repository, [string]$Output, [string]$Revision)
    Push-Location $Repository
    try {
        Invoke-Native dotnet @('tool', 'restore', '--tool-manifest', 'dotnet-tools.json')
        Invoke-Native dotnet @('restore', '--locked-mode')
        Invoke-Native dotnet @('publish', 'src/DocumentStorage.Web', '-c', 'Release', '--no-restore', '--self-contained', 'false', '-o', $Output, "-p:InformationalVersion=$Revision", '-p:IncludeSourceRevisionInInformationalVersion=false')
    } finally { Pop-Location }
}

function Get-CatalogBackupArguments {
    param([string]$ConnectionString, [string]$BackupPath)
    $connection = New-Object System.Data.Common.DbConnectionStringBuilder
    # PowerShell's dictionary adapter turns .ConnectionString = ... into an entry
    # named ConnectionString. Call the CLR setter to actually parse the settings.
    $connection.set_ConnectionString($ConnectionString)
    foreach ($key in @('Host', 'Port', 'Username', 'Database')) {
        if (-not $connection.ContainsKey($key) -or [string]::IsNullOrWhiteSpace([string]$connection[$key])) {
            throw "Catalog connection settings must include $key before a backup can run."
        }
    }
    return @('--host', [string]$connection['Host'], '--port', [string]$connection['Port'], '--username', [string]$connection['Username'], '--dbname', [string]$connection['Database'], '--no-password', '--format=custom', '--file', $BackupPath)
}
