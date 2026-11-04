# Once per Windows instance. Task Scheduler stores the administrator password; the web app never sees it.
[CmdletBinding()]
param([string]$InstallRoot = 'C:\Apps\DocumentStorage', [string]$PostgresBin = 'C:\Program Files\PostgreSQL\18\bin')
. (Join-Path $PSScriptRoot 'common.ps1')
Assert-Administrator
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$repo = Join-Path $InstallRoot 'repo'
if ((Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path -ne $repo -or -not (Test-Path (Join-Path $InstallRoot 'instance.json'))) { throw 'Run from the installed instance repo.' }
if ($InstallRoot -match '["\r\n]' -or $PostgresBin -match '["\r\n]') { throw 'Invalid task paths.' }
$service = Get-CimInstance Win32_Service -Filter "Name='DocumentStorage'"
if ($null -eq $service -or $service.PathName -notlike ('*"' + (Join-Path $InstallRoot 'publish\DocumentStorage.Web.dll') + '"*') -or $service.StartName -notin @('NT AUTHORITY\LocalService','NT AUTHORITY\Local Service')) { throw 'Expected this instance DocumentStorage service running as LocalService.' }
$name = (Split-Path $InstallRoot -Leaf) + ' updater'
$existing = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
if ($existing -and $existing.State -in @('Running','Queued')) { throw 'Wait for the running updater to finish before reinstalling it.' }
$directory = Join-Path $InstallRoot 'updater'
Protect-Directory $InstallRoot
Protect-Directory $directory
Protect-Directory (Join-Path $directory 'requests') -ServiceCanModify
# Clear an unclaimed request only during deliberate administrator repair, never from a timeout in the app.
$lock = [IO.File]::Open((Join-Path $directory 'run.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try { Remove-Item -LiteralPath (Join-Path $directory 'requests\pending.json') -ErrorAction SilentlyContinue }
finally { $lock.Dispose() }
$credential = Get-Credential -UserName "$env:USERDOMAIN\$env:USERNAME" -Message 'Windows administrator password for the DocumentStorage updater task (not app or database login).'
if ($null -eq $credential) { throw 'No credential supplied.' }
try {
    $worker = Join-Path $repo 'tools\deploy\updater.ps1'
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $worker + '" -InstallRoot "' + $InstallRoot + '" -PostgresBin "' + $PostgresBin + '"'
    $action = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Argument $arguments -WorkingDirectory $repo
    # Queue covers a click arriving just after the previous result, before that task has exited.
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 1) -MultipleInstances Queue -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $name -Action $action -Settings $settings -RunLevel Highest -Force -User $credential.UserName -Password $credential.GetNetworkCredential().Password | Out-Null
    # LocalService may read/run this task, never change its executable, arguments or identity.
    $scheduler = New-Object -ComObject 'Schedule.Service'; $scheduler.Connect()
    $task = $scheduler.GetFolder('\').GetTask($name)
    $security = New-Object Security.AccessControl.RawSecurityDescriptor($task.GetSecurityDescriptor(4))
    $sid = New-Object Security.Principal.SecurityIdentifier('S-1-5-19')
    $ace = New-Object Security.AccessControl.CommonAce([Security.AccessControl.AceFlags]::None,[Security.AccessControl.AceQualifier]::AccessAllowed,0x1200a9,$sid,$false,$null)
    $security.DiscretionaryAcl.InsertAce($security.DiscretionaryAcl.Count,$ace)
    $task.SetSecurityDescriptor($security.GetSddlForm([Security.AccessControl.AccessControlSections]::Access),0)
    . (Join-Path $PSScriptRoot 'update-protocol.ps1')
    Write-UpdateJson (Join-Path $directory 'installed.json') @{ protocol=1; task=$name }
    # A stopped/crashed task can leave a Running status. Repair makes that failure explicit.
    if (Test-Path (Join-Path $directory 'status.json')) {
        $status = Get-Content (Join-Path $directory 'status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($status.state -in @('Running','Queued')) { $status.state='Failed'; $status.stage='Updater reinstalled. Check for updates again.'; Write-UpdateJson (Join-Path $directory 'status.json') $status }
    }
} finally { $credential=$null }
Write-Host "Registered '$name'. Settings > Updates can now check and install updates. Re-run this installer after changing the task account's Windows password."
