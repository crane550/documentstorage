# Windows server and Cloudflare deployment

Target: **https://stash.instrumentsofliberty.com**, a separate **DocumentStorage** Windows service, PostgreSQL database/role **documentstorage**, and origin **http://127.0.0.1:5240**.

The app uses its **own ASP.NET Identity username/password login**, like the accounting app. Users, password hashes and roles live in this application's database. Its sign-in cookie and key ring are separate from the other applications. Five failed attempts lock an account for 15 minutes. Account → Change password/Sign out manages your own session. There is no public self-registration; the installer creates the first administrator.

**Cloudflare Tunnel only provides the route and HTTPS. No Cloudflare Access application, team domain or Audience tag is required.** If an Access application was created during the earlier setup, it can be removed once the built-in login is verified; keep the tunnel route. Cloudflare Access may be added later as an optional extra gate, not as the app's login.

These scripts are for the same Windows server as the accounting app. They do not modify its services, databases, ports, files or routes. This cloud workspace has no administrative connection to the server; run the following commands there.

## 1. Server prerequisites

Open an elevated PowerShell window on the Windows server.

- The Blazor asset package is explicitly pinned in the project so SDK maintenance updates do not change the committed dependency lock. Keep locked restore enabled.

- Git and .NET SDK **10.0.401 or later**. New SDK install if needed:

  ```powershell
  winget install --id Microsoft.DotNet.SDK.10 --exact --accept-source-agreements --accept-package-agreements
  ```

- PostgreSQL 18, with a **new** application role/database. Do not reuse the accounting role or database.
- A machine-wide Poppler installation. WinGet verifies the downloaded archive hash:

  ```powershell
  winget install --id oschwartz10612.Poppler --exact --scope machine --accept-source-agreements --accept-package-agreements
  $pdf = Get-ChildItem "$env:ProgramFiles\WinGet\Packages" -Filter pdftotext.exe -File -Recurse |
      Select-Object -First 1 -ExpandProperty FullName
  if (-not $pdf) { throw 'Locate the machine-wide pdftotext.exe installation before continuing.' }
  & $pdf -v
  ```

Create the database from an interactive `psql` session:

```powershell
& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -U postgres -d postgres
```

Inside `psql` (if already created, keep the existing role/database):

```sql
CREATE ROLE documentstorage LOGIN;
\password documentstorage
CREATE DATABASE documentstorage OWNER documentstorage;
\q
```

Enter a new **database role** password at the secure `\password` prompt. The installer requests it again. Your app administrator login is a separate credential, chosen during installation.

## 2. Clone or update, then install

For a new checkout:

```powershell
git clone https://github.com/crane550/documentstorage.git C:\Apps\DocumentStorage\repo
```

Then, in the same elevated PowerShell window:

```powershell
Set-Location C:\Apps\DocumentStorage\repo
git pull --ff-only origin main
powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy\preflight.ps1

$pdf = Get-ChildItem "$env:ProgramFiles\WinGet\Packages" -Filter pdftotext.exe -File -Recurse |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $pdf) { throw 'Set $pdf to the installed pdftotext.exe path.' }

powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy\install.ps1 -PdfTextCommand $pdf
```

Two local credential dialogs appear:

1. The `documentstorage` **PostgreSQL role** password created above.
2. Your first **app administrator** username and password. Choose a password of at least 8 characters. This is the login you will use in the browser.

The installer refuses an existing instance/service rather than overwriting it. It publishes the clean checkout, restricts the installation directory to Administrators/SYSTEM/LocalService, checks the new database connection, and saves private configuration outside Git at `C:\Apps\DocumentStorage\instance.json`. The database password remains in this protected file. The one-time administrator password is removed after successful startup; PostgreSQL stores its Identity password hash. Startup never resets an existing account's password.

The Windows service runs as **LocalService**. It can read configuration/binaries and write only its `keys` and initial `storage` directories. Data Protection keys persist outside release folders and are protected by Windows DPAPI. Other storage roots need their own filesystem permissions.

The installer waits for the service, database and **exact deployed revision** to report healthy:

```powershell
Get-Service DocumentStorage
Invoke-RestMethod http://127.0.0.1:5240/health
```

`/health` is anonymous and exposes readiness/revision/version/build. `/login` is the app's public sign-in page. Downloads and Blazor connections require a valid app cookie. Public cookies are HTTPS-only, so use the public hostname to sign in; plain loopback HTTP is for the health check. Logs are in the Windows Application event log under DocumentStorage.

The scripts' syntax and application behavior are validated in cloud tests. Actual Windows service installation and ACL behavior still require validation on this server.

## 3. Route the tunnel

```powershell
Get-Service cloudflared
```

Reuse the existing connector **on this server** if it already serves your applications. Creating a tunnel in the dashboard alone does not connect it to this server. Check that the selected tunnel has a healthy connector on the correct machine. Do not replace an existing connector's token or reinstall its service just to add another hostname.

Add one published application route to that tunnel:

| Setting | Value |
| --- | --- |
| Subdomain | `stash` |
| Domain | `instrumentsofliberty.com` |
| Service type | HTTP |
| Service URL | `127.0.0.1:5240` |

Keep HTTPS at Cloudflare and HTTP only on loopback between cloudflared and the app. No router forwarding or inbound firewall rule for port 5240 is needed. Leave all existing application routes and unrelated DNS records intact. If `stash` already has a DNS record, inspect it before changing it.

Enable WebSockets, require HTTPS, and bypass caching for this hostname. The app also sends `Cache-Control: no-store`. Forwarded scheme/client headers are trusted only from the local connector; request hosts must match the configured public name or loopback.

## 4. Verify

From another device, open `https://stash.instrumentsofliberty.com` in a private window:

1. The app's cardboard-themed **Sign in** page must appear. Sign in with the app administrator credentials chosen during install.
2. Register `C:\Apps\DocumentStorage\storage` as an initial root and use made-up documents to check upload, text search, download, rename, trash and restore. A rendered page alone does not confirm the Blazor WebSocket works.
3. Open Account, change your password if desired, and sign out. A fresh anonymous browser must return to login; anonymous original-file downloads must be refused.
4. Restart DocumentStorage and verify login and file operations again. Check automatic startup after a planned server restart when convenient.

Before registering existing document directories, grant LocalService Modify rights **only on the intended roots** and establish document backups. Interactive mapped drives are not available to services. NAS access later needs a dedicated service identity with share and filesystem permissions.

## Updating

After the one-time [browser updater setup](../../docs/updating.md), use **Update** at the bottom of the menu or **Settings › Updates**. First deploy this version with the console commands below, then run `powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy\install-updater.ps1` once in the same elevated window. It requests the Windows administrator password for Task Scheduler, not the app/database login. Re-run after that Windows password changes. The following console workflow remains available for initial setup and recovery.

```powershell
Set-Location C:\Apps\DocumentStorage\repo
git pull --ff-only origin main
powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy\deploy.ps1
```

The connection-string/backup argument regression check runs with `powershell -NoProfile -ExecutionPolicy Bypass -File tests\deploy\backup-arguments.ps1` (or `pwsh` on Linux). It uses synthetic settings and does not contact a database or alter services.

Updates build a staging release, make/check a custom-format PostgreSQL backup, stop only DocumentStorage, retain the old binaries, switch, and verify the new revision. Startup failure triggers an attempted binary rollback. Database snapshots are not document backups; binary rollback does not reverse migrations. An incompatible migration needs deliberate recovery from the retained backup. The browser updater uses this same deployment script through its fixed scheduled task.

| Symptom | Check |
| --- | --- |
| Service exits immediately | Protected instance configuration, database access, Application event log |
| Login fails after five bad attempts | Wait 15 minutes; keep app login separate from the PostgreSQL role password |
| Cloudflare 502 | DocumentStorage service, `/health`, HTTP route and port 5240 |
| Cloudflare 1033 | Selected tunnel's connector and its outbound connection |
| Page loads but controls do nothing | WebSockets, `/_blazor` requests, cache bypass, browser errors |
| Login works until restart/update | Persistent Data Protection keys, service identity and file permissions |
| Folder unavailable | Service-account rights and whether the storage is actually mounted |

## Image OCR

After updating, follow [Image OCR setup](../../docs/image-ocr.md). Install `UB-Mannheim.TesseractOCR` machine-wide, then run `tools\deploy\setup-ocr.ps1` in elevated PowerShell. It checks language data, grants the existing LocalService identity read/execute access to the dedicated tool folder, preserves instance settings and restarts only DocumentStorage. In Settings → File formats, **Check OCR engine** confirms what the service can actually run. Missing OCR does not stop the app or previewing; queued images wait to retry. Magick.NET image decoding is bundled with normal .NET publish.
