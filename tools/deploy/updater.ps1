# Fixed scheduled-task entry point. No command, path, branch or repository comes from the web request.
[CmdletBinding()]
param([string]$InstallRoot = 'C:\Apps\DocumentStorage', [string]$PostgresBin = 'C:\Program Files\PostgreSQL\18\bin')
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'update-protocol.ps1')
Assert-Administrator
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$repo = Join-Path $InstallRoot 'repo'
if ((Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path -ne $repo) { throw 'Updater must run from this instance repository.' }
$directory = Join-Path $InstallRoot 'updater'
$requestPath = Join-Path $directory 'requests\pending.json'
$statusPath = Join-Path $directory 'status.json'
$checkPath = Join-Path $directory 'check.json'
# A second task/manual invocation must not replace the first request or status.
try { $lock = [IO.File]::Open((Join-Path $directory 'run.lock'), 'OpenOrCreate', 'ReadWrite', 'None') } catch { exit 1 }
$status = $null
$stage = 'Validating the update request'
try {
    if (-not (Test-Path -LiteralPath $requestPath)) { exit 0 }
    $request = Read-UpdateRequest $requestPath
    $status = [ordered]@{ id=$request.id; mode=$request.mode; state='Running'; stage=$stage; running=$request.running; latest=$request.target; updatedAt=[DateTimeOffset]::UtcNow.ToString('o'); pending=@(); databaseChange=$false; moreChanges=$false }
    Write-UpdateJson $statusPath $status
    # Publish Running before consuming the request, so all app processes see the same lock state.
    Remove-Item -LiteralPath $requestPath
    $env:GIT_TERMINAL_PROMPT = '0'
    $env:GCM_INTERACTIVE = 'Never'
    $stage = 'Verifying the fixed repository origin'
    $remote = & git -C $repo remote get-url origin
    if ($LASTEXITCODE -ne 0 -or $remote -cne 'https://github.com/crane550/documentstorage.git') { throw 'The repository origin is not the configured application repository.' }
    $stage = 'Checking for local repository changes'
    $changes = & git -C $repo status --porcelain
    if ($LASTEXITCODE -ne 0 -or $changes) { throw 'The repository contains local changes. Preserve them before updating.' }
    $stage = 'Loading complete release history'
    if ((& git -C $repo rev-parse --is-shallow-repository) -eq 'true') { Invoke-Native git @('-C',$repo,'fetch','--unshallow','origin') }
    $stage = 'Checking main for a newer release'
    $status.stage=$stage; $status.updatedAt=[DateTimeOffset]::UtcNow.ToString('o'); Write-UpdateJson $statusPath $status
    Invoke-Native git @('-C',$repo,'fetch','--quiet','--no-tags','origin','main')
    $latest = & git -C $repo rev-parse --verify 'refs/remotes/origin/main^{commit}'
    if ($LASTEXITCODE -ne 0 -or $latest -cnotmatch '^[0-9a-f]{40}$') { throw 'Cannot identify main.' }
    $stage = 'Verifying release ancestry and changes'
    $comparison = Get-UpdateChanges $repo $request.running $latest
    $status.latest=$latest; $status.pending=@($comparison.pending); $status.databaseChange=$comparison.databaseChange; $status.moreChanges=$comparison.moreChanges
    if ($request.mode -eq 'check') {
        $status.state='Succeeded'; $status.stage='Release check complete'; $status.updatedAt=[DateTimeOffset]::UtcNow.ToString('o')
        Write-UpdateJson $checkPath $status
    } else {
        $stage = 'Revalidating the approved release'
        $check = Get-Content -LiteralPath $checkPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($check.id -ne $request.checkId -or $check.state -ne 'Succeeded' -or $check.running -ne $request.running -or $check.latest -ne $request.target -or
            ([DateTimeOffset]$check.updatedAt) -lt [DateTimeOffset]::UtcNow.AddMinutes(-30) -or $latest -ne $request.target -or $latest -eq $request.running) { throw 'The approved release changed or expired. Check again.' }
        if ((Invoke-RestMethod 'http://127.0.0.1:5240/health' -TimeoutSec 5).revision -ne $request.running) { throw 'The running release changed. Check again.' }
        $stage = 'Fast-forwarding the checkout to the approved release'
        Move-ToApprovedRelease $repo $latest
        # Same staged build, required verified catalog backup, scoped restart and rollback as console deployment.
        & (Join-Path $PSScriptRoot 'deploy.ps1') -InstallRoot $InstallRoot -PostgresBin $PostgresBin -UpdateStatusPath $statusPath
        $status.state='Succeeded'; $status.stage='Update installed and the new release is healthy'; $status.updatedAt=[DateTimeOffset]::UtcNow.ToString('o')
    }
    Write-UpdateJson $statusPath $status
} catch {
    # Browser status deliberately excludes raw tool output, credentials and machine paths.
    if ($null -eq $status) { $status=[ordered]@{ id=''; mode='check'; state='Failed'; stage=''; running=''; latest=''; updatedAt=''; pending=@(); databaseChange=$false; moreChanges=$false } }
    elseif (Test-Path $statusPath) { $savedStage=(Get-Content $statusPath -Raw -Encoding UTF8 | ConvertFrom-Json).stage; if ($savedStage -in @('Building the new release while the app stays online','Backing up and verifying the catalog before restart','Restarting DocumentStorage with the new release','Verifying the new release and database','Deployment failed; restoring the previous binaries')) { $stage=$savedStage } }
    $status.state='Failed'; $status.stage="Could not finish: $stage. Check again or inspect the updater on the server."; $status.updatedAt=[DateTimeOffset]::UtcNow.ToString('o')
    Write-UpdateJson $statusPath $status
    Remove-Item -LiteralPath $requestPath -ErrorAction SilentlyContinue
    # Only administrators can read this diagnostic. Never dump configuration or process environment.
    [IO.File]::WriteAllText((Join-Path $directory 'failure.txt'), "Failure during $stage`r`n" + $_.Exception.GetType().FullName)
    exit 1
} finally { $lock.Dispose() }
