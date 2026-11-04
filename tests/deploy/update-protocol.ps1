. (Join-Path $PSScriptRoot '../../tools/deploy/update-protocol.ps1')
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('documentstorage-update-' + [Guid]::NewGuid())
New-Item -ItemType Directory $temporary | Out-Null
$count=0
function Check([bool]$Valid,[string]$Label) { if (-not $Valid) { throw "FAIL $Label" }; $script:count++; Write-Host "PASS $Label" }
try {
    $request=@{ id=('1'*32); mode='check'; running=('a'*40); target=''; checkId=''; actor='synthetic-admin'; requestedAt=[DateTimeOffset]::UtcNow.ToString('o') }
    $file=Join-Path $temporary 'request.json'
    Write-UpdateJson $file $request
    Check ((Read-UpdateRequest $file).mode -eq 'check') 'fresh check requests parse on Windows PowerShell and PowerShell 7'
    foreach ($change in @(@{mode='shell'},@{running='main; whoami'},@{target='main'},@{requestedAt=[DateTimeOffset]::UtcNow.AddMinutes(-6).ToString('o')},@{command='run'})) {
        $invalid=$request.Clone();foreach($key in $change.Keys) { $invalid[$key]=$change[$key] };Write-UpdateJson $file $invalid
        $refused=$false;try {Read-UpdateRequest $file | Out-Null} catch {$refused=$true};Check $refused ('request rejects invalid '+($change.Keys -join ','))
    }
    $request.mode='update';$request.target=('b'*40);$request.checkId=('2'*32);Write-UpdateJson $file $request
    Check ((Read-UpdateRequest $file).target -eq ('b'*40)) 'updates require an exact target and correlated release check'
    # Repeated atomic replacements never publish partial JSON or leave temporary files.
    1..20 | ForEach-Object {Write-UpdateJson $file @{number=$_};$read=Get-Content $file -Raw | ConvertFrom-Json;Check ($read.number -eq $_) 'atomic status replacement is readable'}
    $repo=Join-Path $temporary 'repo';git init --quiet $repo
    git -C $repo config user.name Synthetic;git -C $repo config user.email synthetic@example.invalid
    [IO.File]::WriteAllText((Join-Path $repo 'sample.txt'),'Synthetic original');git -C $repo add .;git -C $repo commit --quiet -m 'Synthetic initial release'
    $base=git -C $repo rev-parse HEAD
    New-Item -ItemType Directory (Join-Path $repo 'src/DocumentStorage.Data/Migrations') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $repo 'src/DocumentStorage.Data/Migrations/Synthetic.cs'),'Synthetic migration marker');git -C $repo add .;git -C $repo commit --quiet -m 'Synthetic schema change'
    $latest=git -C $repo rev-parse HEAD
    $result=Get-UpdateChanges $repo $base $latest
    Check ($result.pending.Count -eq 1 -and $result.databaseChange -and $result.pending[0].subject -eq 'Synthetic schema change') 'real Git history detects pending releases and catalog migrations'
    $result=Get-UpdateChanges $repo $latest $latest
    Check ($result.pending.Count -eq 0 -and -not $result.databaseChange) 'a current release reports zero changes'
    $refused=$false;try{Get-UpdateChanges $repo $latest $base | Out-Null}catch{$refused=$true};Check $refused 'downgrades and divergent histories cannot be offered as updates'
    [IO.File]::WriteAllText((Join-Path $repo 'unpublished.txt'),'Synthetic unpublished change');git -C $repo add .;git -C $repo commit --quiet -m 'Synthetic local-only commit'
    $refused=$false;try{Move-ToApprovedRelease $repo $latest | Out-Null}catch{$refused=$true};Check $refused 'an ahead checkout cannot silently deploy an unapproved local commit'
    git -C $repo reset --quiet --hard $base
    Move-ToApprovedRelease $repo $latest | Out-Null
    Check ((git -C $repo rev-parse HEAD) -eq $latest) 'a valid fast-forward ends on exactly the approved commit'
    Write-Host "All $count updater protocol checks passed."
} finally {Remove-Item -LiteralPath $temporary -Recurse -Force}
