# Exercises the real deploy.ps1 control flow with synthetic disk state and stand-ins for Windows services/tools.
$ErrorActionPreference='Stop'
$source=Join-Path $PSScriptRoot '../../tools/deploy'
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('documentstorage-deploy-'+[Guid]::NewGuid())
New-Item -ItemType Directory $temporary | Out-Null
$count=0
function Check([bool]$Valid,[string]$Label) { if(-not $Valid){throw "FAIL $Label"};$script:count++;Write-Host "PASS $Label" }
try {
    foreach($scenario in @('success','build-failure','backup-failure','health-failure')) {
        $global:SyntheticRoot=Join-Path $temporary $scenario
        $repo=Join-Path $global:SyntheticRoot 'repo';$scripts=Join-Path $repo 'tools/deploy';New-Item -ItemType Directory $scripts -Force | Out-Null
        foreach($name in @('deploy.ps1','common.ps1','update-protocol.ps1')){Copy-Item (Join-Path $source $name) (Join-Path $scripts $name)}
        # Override only machine boundaries; the actual deployment algorithm runs unchanged.
        Add-Content -LiteralPath (Join-Path $scripts 'common.ps1') -Value @'
function Assert-Administrator {}
function Get-Revision { return ('b'*40) }
function Protect-Directory { param($Path,[switch]$ServiceCanModify);New-Item -ItemType Directory $Path -Force | Out-Null }
function Publish-Release { param($Repository,$Output,$Revision);$global:SyntheticEvents.Add('build');if($global:SyntheticScenario -eq 'build-failure'){throw 'Synthetic build failure'};New-Item -ItemType Directory $Output -Force | Out-Null;[IO.File]::WriteAllText((Join-Path $Output 'payload.txt'),'new') }
function Get-CimInstance { param($Filter,$ClassName);return @{PathName=('dotnet "'+(Join-Path $global:SyntheticRoot 'publish/DocumentStorage.Web.dll')+'"')} }
function Invoke-Native { param($File,$Arguments);if($File.EndsWith('pg_dump.exe')){$global:SyntheticEvents.Add('backup');[IO.File]::WriteAllText($Arguments[-1],'Synthetic catalog backup')}else{$global:SyntheticEvents.Add('verify');if($global:SyntheticScenario -eq 'backup-failure'){throw 'Synthetic backup validation failure'}} }
function Invoke-RestMethod { param($Uri,$TimeoutSec);return @{revision=('a'*40)} }
function Get-Service { param($Name);$service=New-Object PSObject;Add-Member -InputObject $service -MemberType ScriptMethod -Name WaitForStatus -Value {param($Status,$Timeout)};return $service }
function Stop-Service { param($Name,$ErrorAction);$global:SyntheticEvents.Add('stop') }
function Start-Service { param($Name,$ErrorAction);$global:SyntheticEvents.Add('start') }
function Wait-Healthy { param($Port,$Revision,$ServiceName);$global:SyntheticEvents.Add('healthy');if($global:SyntheticScenario -eq 'health-failure' -and $Revision -eq ('b'*40)){throw 'Synthetic health failure'} }
'@
        $global:SyntheticEvents=New-Object 'System.Collections.Generic.List[string]';$global:SyntheticScenario=$scenario
        $publish=Join-Path $global:SyntheticRoot 'publish';New-Item -ItemType Directory $publish | Out-Null
        [IO.File]::WriteAllText((Join-Path $publish 'payload.txt'),'old')
        $postgres=Join-Path $global:SyntheticRoot 'postgres';New-Item -ItemType Directory $postgres | Out-Null
        foreach($name in @('pg_dump.exe','pg_restore.exe')){[IO.File]::WriteAllText((Join-Path $postgres $name),'Synthetic placeholder')}
        @{Hosting=@{PublicOrigin='https://synthetic.example.test'};ConnectionStrings=@{Catalog='Host=localhost;Port=55432;Database=synthetic;Username=synthetic'};DOCUMENTSTORAGE_DB_PASSWORD='synthetic-only'}|ConvertTo-Json|Set-Content (Join-Path $global:SyntheticRoot 'instance.json')
        $progress=@{}
        if($scenario -eq 'health-failure') {
            $statusDirectory=Join-Path $global:SyntheticRoot 'updater';New-Item -ItemType Directory $statusDirectory | Out-Null
            $statusFile=Join-Path $statusDirectory 'status.json';[IO.File]::WriteAllText($statusFile,'Synthetic invalid progress JSON')
            $progress=@{ UpdateStatusPath=$statusFile }
        }
        $failed=$false;try{& (Join-Path $scripts 'deploy.ps1') -InstallRoot $global:SyntheticRoot -PostgresBin $postgres @progress}catch{$failed=$true}
        $payload=Get-Content (Join-Path $publish 'payload.txt') -Raw
        if($scenario -eq 'success') {
            Check (-not $failed -and $payload -eq 'new' -and ($global:SyntheticEvents -join ',') -eq 'build,backup,verify,stop,start,healthy') 'deployment builds and verifies its backup before stopping only its service'
            Check (@(Get-ChildItem (Join-Path $global:SyntheticRoot 'releases') -Filter payload.txt -Recurse | Where-Object {(Get-Content $_.FullName -Raw) -eq 'old'}).Count -eq 1) 'successful deployment retains previous binaries'
        } elseif($scenario -eq 'health-failure') {
            Check ($failed -and $payload -eq 'old') 'failed health check restores previous binaries even when progress reporting fails'
            Check (@(Get-ChildItem (Join-Path $global:SyntheticRoot 'backups') -Filter '*.dump').Count -eq 1) 'rollback retains the catalog backup without automatically restoring it over later writes'
        } else { Check ($failed -and $payload -eq 'old' -and -not $global:SyntheticEvents.Contains('stop')) "$scenario leaves the current release running and untouched" }
    }
    Write-Host "All $count deployment workflow checks passed (Windows process boundaries simulated)."
} finally {Remove-Item -LiteralPath $temporary -Recurse -Force}
