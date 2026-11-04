# Shared protocol helpers; also exercised on Linux with synthetic files/Git repositories.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Write-UpdateJson {
    param([string]$Path, $Value)
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $text = $Value | ConvertTo-Json -Depth 6 -Compress
        [IO.File]::WriteAllText($temporary, $text, (New-Object Text.UTF8Encoding($false)))
        # Both files are on one volume. Replace publishes a complete status, even across restart.
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $Path) }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}
function Read-UpdateRequest {
    param([string]$Path)
    if ((Get-Item -LiteralPath $Path).Length -gt 4096) { throw 'Invalid update request size.' }
    $request = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $names = @($request.PSObject.Properties.Name | Sort-Object)
    if (($names -join ',') -ne 'actor,checkId,id,mode,requestedAt,running,target') { throw 'Invalid update request fields.' }
    foreach ($name in @('id','mode','running','target','checkId','actor')) {
        if ($request.$name -isnot [string]) { throw 'Invalid update request value.' }
    }
    if ($request.id -cnotmatch '^[0-9a-f]{32}$' -or $request.mode -cnotmatch '^(check|update)$' -or $request.running -cnotmatch '^[0-9a-f]{40}$' -or
        $request.actor.Length -lt 1 -or $request.actor.Length -gt 256) { throw 'Invalid update request.' }
    $requested = [DateTimeOffset]$request.requestedAt
    if ($requested -lt [DateTimeOffset]::UtcNow.AddMinutes(-5) -or $requested -gt [DateTimeOffset]::UtcNow.AddMinutes(1)) { throw 'The update request expired.' }
    if ($request.mode -eq 'update') {
        if ($request.target -cnotmatch '^[0-9a-f]{40}$' -or $request.checkId -cnotmatch '^[0-9a-f]{32}$') { throw 'Invalid approved release.' }
    } elseif ($request.target -ne '' -or $request.checkId -ne '') { throw 'Invalid check request.' }
    return $request
}
function Get-UpdateChanges {
    param([string]$Repository, [string]$Running, [string]$Latest)
    foreach ($revision in @($Running,$Latest)) { if ($revision -cnotmatch '^[0-9a-f]{40}$') { throw 'Full commit IDs are required.' } }
    & git -C $Repository merge-base --is-ancestor $Running $Latest
    if ($LASTEXITCODE -ne 0) { throw 'The running release is not an ancestor of main. No downgrade or divergent update is allowed.' }
    $lines = @(& git -C $Repository log -51 --format='%H%x09%s' "$Running..$Latest")
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read release changes.' }
    $pending = @($lines | Select-Object -First 50 | ForEach-Object {
        $parts = $_ -split "`t", 2
        @{ commit = $parts[0]; subject = $parts[1].Substring(0,[Math]::Min(300,$parts[1].Length)) }
    })
    $files = @(& git -C $Repository diff --name-only $Running $Latest -- src/DocumentStorage.Data/Migrations)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect migration changes.' }
    return @{ pending = $pending; databaseChange = [bool]$files; moreChanges = ($lines.Count -gt 50) }
}
function Move-ToApprovedRelease {
    param([string]$Repository, [string]$Target)
    if ($Target -cnotmatch '^[0-9a-f]{40}$') { throw 'An exact approved release is required.' }
    & git -C $Repository merge-base --is-ancestor HEAD $Target
    if ($LASTEXITCODE -ne 0) { throw 'The checkout is ahead of or diverged from the approved release. Nothing was deployed.' }
    & git -C $Repository merge --ff-only $Target
    if ($LASTEXITCODE -ne 0) { throw 'Cannot fast-forward to the approved release.' }
    $head = & git -C $Repository rev-parse --verify 'HEAD^{commit}'
    if ($LASTEXITCODE -ne 0 -or $head -cne $Target) { throw 'The checkout does not match the approved release.' }
}
