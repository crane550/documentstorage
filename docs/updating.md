# Browser updates and release numbers

The menu footer shows the version, build number and short Git revision of the running binaries. Like Beans, the version is `major.milestone.build` (initial series `0.3`), with the build automatically taken from the full Git history count and padded to three digits. It changes when a new commit is built; a repository pull alone does not change the running display. `Directory.Build.props` selects the series. Builds from source archives fall back to build zero and cannot update from the browser.

**Update** at the bottom of the menu opens **Settings › Updates**. Choose **Check for updates**, review the latest 50 commit subjects and whether catalog migrations are included, then **Install update…** and **Back up & install**. Approval applies to that exact commit, not whatever main might contain later. A changed release requires another check. Checks expire after 30 minutes. New releases must descend from the running commit; local modifications, divergent history or an ahead checkout block deployment.

The page reports checking, building, backing up, restarting, verifying and success/failure. The build and verified PostgreSQL backup finish before DocumentStorage stops. A failed build or backup leaves the existing release running. Startup failure attempts a binary rollback and retains the catalog backup. Document bytes stay in their folders; these catalog snapshots do not back up documents and are never restored automatically over newer changes. Save work in other tabs before installing. A browser-side watcher reloads Updates after a healthy release returns, even when the old Blazor circuit was lost.

## One-time server setup

The app cannot install itself while its service is stopped, so a separate Windows scheduled task performs deployment. An administrator must install it once, as with Beans. First deploy this feature using the existing console workflow, then run in an elevated PowerShell on the Windows server:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Apps\DocumentStorage\repo\tools\deploy\install-updater.ps1
```

Enter the **Windows administrator** account password, not the application or PostgreSQL password. Task Scheduler stores it; no password is sent through the browser or saved in the request. The account needs the same Git and build access as a manual deployment. Re-run the installer after changing its Windows password. Nondefault installation or PostgreSQL locations can be supplied through `-InstallRoot` / `-PostgresBin`; the installed task fixes those paths. The current service contract is the DocumentStorage service on loopback 5240, running as LocalService.

After this setup, future updates use the browser. The console `deploy.ps1` remains available for recovery. Actual Windows Task Scheduler, ACL, Git credentials, service stop/start and tunnel reconnect behavior must be verified on the server; Linux checks simulate those machine boundaries.

## Boundaries and recovery

- Browser actions use the app administrator cookie, antiforgery and current role/lockout checks. MCP bearer credentials cannot access update status or start deployments. There is no MCP update tool.
- The LocalService app can read/run only the installed fixed task; it cannot change its action/account. Its only writable updater area is `updater/requests`. Scripts, configuration, status and release folders remain read-only to the app. Never grant it write access to the repository, published binaries, instance configuration or task definition.
- A request contains an ID, fixed `check`/`update` mode, full running/target commit IDs, correlated check ID, actor and timestamp. No shell command, branch, executable or path comes from the browser. The worker validates origin as `https://github.com/crane550/documentstorage.git`, fetches main, verifies ancestry and exact target, and uses the existing deployment script.
- Durable pending/running state blocks duplicate requests across browser tabs and restarts. The worker also takes an exclusive filesystem lock. Task Scheduler queues a request arriving just after another finishes; a redundant task with no pending request does nothing.
- A timeout does not trigger another deployment. If a task was killed, cannot log in or stays stuck, inspect **DocumentStorage updater** in Task Scheduler. Re-run the installer only after the task has stopped. It repairs permissions, clears an unclaimed request and marks an interrupted running status failed. If the app itself is unavailable, inspect the service/retained releases and catalog backup before using the console recovery workflow.
- Browser progress intentionally excludes raw tool logs, database credentials and physical paths. `updater/failure.txt` records the failed stage and exception type for server administrators without dumping configuration.

`/health` still reports the exact running commit in `revision`, and now also reports `version` and `build`. These are assembly metadata; health checks never substitute the checked-out repository HEAD for the running binary revision.

## Verification

The real HTTP/authentication checks exercise cookie-only access, antiforgery, current administrator rights, exact/stale approval, durable requests and duplicate/startup-failure behavior. MCP checks refuse bearer updater calls. PowerShell protocol checks use real synthetic Git history; deployment checks run the actual deploy algorithm against synthetic files with stand-ins for Windows processes/services. No server task is registered by cloud tests.

```sh
dotnet run --project tests/DocumentStorage.AuthenticationChecks -c Release
dotnet run --project tests/DocumentStorage.McpChecks -c Release
pwsh -NoProfile -File tests/deploy/update-protocol.ps1
pwsh -NoProfile -File tests/deploy/update-deployment.ps1
# Separate executable fixture; no mock updater configuration is exposed in the production app:
dotnet run --project tests/DocumentStorage.AuthenticationChecks -c Release -- --updater-browser
# In another terminal:
node tests/browser/updates.mjs
```

The browser fixture recreates only `documentstorage_updater_browser_checks` on loopback, uses synthetic ignored storage and the ignored browser credential fixture, and listens on 5248. It checks the footer, changes, escaped subjects, cancel/approve, progress, mobile layout and restart reload. It cannot invoke a real Windows task or deploy anything.
