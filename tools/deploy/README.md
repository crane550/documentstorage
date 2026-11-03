# Windows server and Cloudflare deployment

Target: **https://stash.instrumentsofliberty.com**, a separate **DocumentStorage** Windows service, PostgreSQL database/role **documentstorage**, and origin **http://127.0.0.1:5240**.

These scripts are for the same Windows server as the accounting app. They do not modify its services, databases, ports, files or tunnel routes. The cloud workspace has no administrative connection to your server or Cloudflare account; run the server commands locally and make the dashboard changes in your account.

The app requires a valid, signed Cloudflare Access application token for every public request, including Blazor connections and downloads. It checks the team issuer, application audience, signature and expiry. `/health` exposes only readiness and build revision. A public request that bypasses Access cannot open the document workspace.

## 1. Prepare Cloudflare Access

Before publishing a tunnel route, create a **self-hosted application** in Cloudflare Zero Trust → Access → Applications:

- Application name: Document Manager (a placeholder display name).
- Public hostname: `stash.instrumentsofliberty.com`, covering the entire hostname.
- Allow policy: only your email and other explicitly authorized people. Use your existing identity provider or email one-time PIN.
- No Everyone, Bypass, or public-path exceptions. The future MCP endpoints do not exist yet; do not copy the accounting app's MCP bypass rules.

Copy two **non-secret configuration identifiers** from Access: your team hostname (for example `yourteam.cloudflareaccess.com`) and the application Audience (AUD) tag. They are needed by the install script. Access's signing keys are fetched over verified HTTPS from that team's `/cdn-cgi/access/certs` endpoint and refreshed automatically when keys rotate.

## 2. Server prerequisites

Open an elevated PowerShell window on the Windows server.

- Install/currently have Git and the .NET 10 SDK. `dotnet --list-sdks` should include 10.0.403 or a later .NET 10 feature band. For a new SDK install:

  ```powershell
  winget install --id Microsoft.DotNet.SDK.10 --exact --accept-source-agreements --accept-package-agreements
  ```

- Reuse the installed PostgreSQL 18 **server**, with a new role/database below. Do not reuse the accounting application's database role/password.
- Install a trusted Windows Poppler distribution and retain its DLLs beside `pdftotext.exe`. Note its actual executable path; the examples use `C:\Tools\poppler\Library\bin\pdftotext.exe`. Verify `& '<your path>\pdftotext.exe' -v` works. The PDF extraction process keeps TLS/download verification intact; these scripts do not download unverified binaries.

Create the new database from an interactive `psql` session. PostgreSQL asks for its existing admin password as needed:

```powershell
& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -U postgres -d postgres
```

Inside `psql`:

```sql
CREATE ROLE documentstorage LOGIN;
\password documentstorage
CREATE DATABASE documentstorage OWNER documentstorage;
\q
```

Use a new password at the `\password` prompt. Enter it again only in the install script's secure credential dialog, never in chat, a command argument or Git.

## 3. Clone and first install

```powershell
git clone https://github.com/crane550/documentstorage.git C:\Apps\DocumentStorage\repo
Set-Location C:\Apps\DocumentStorage\repo
powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy\preflight.ps1

powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy\install.ps1 `
  -AccessTeamDomain 'YOURTEAM.cloudflareaccess.com' `
  -AccessAudience 'YOUR-APPLICATION-AUD-TAG' `
  -PdfTextCommand 'C:\Tools\poppler\Library\bin\pdftotext.exe'
```

Replace the two Access identifiers and the PDF tool path. The existing Windows Git credential manager can supply GitHub authentication; the script does not request a token.

The installer:

1. Refuses an existing instance/service instead of overwriting it.
2. Publishes the clean checkout with the exact Git revision embedded in `/health`.
3. Restricts the instance directory to Administrators, SYSTEM and LocalService before writing credentials.
4. Checks the **new** PostgreSQL role/database using the password entered through `Get-Credential`.
5. Writes protected per-instance configuration to `C:\Apps\DocumentStorage\instance.json`. This file contains the database password, stays outside the checkout and must not be shared. Environment overrides can change settings; do not set machine-wide overrides for this instance.
6. Creates `keys` and an initial `storage` directory, writable by LocalService. Other instance files/binaries are read-only for the service. Data Protection keys persist across builds and are protected by Windows DPAPI.
7. Creates and starts the `DocumentStorage` Windows service as LocalService, then verifies its process, database readiness and exact release revision on port 5240.

Check locally:

```powershell
Get-Service DocumentStorage
Invoke-RestMethod http://127.0.0.1:5240/health
```

The health response must say `ready`. Visiting `/` directly without an Access token returns **401** in this production configuration; that is expected. Logs are available in the Windows Application event log. The scripts' syntax and application behavior are validated in Linux cloud tests; installing/running a Windows service and its ACLs still require validation on this server.

## 4. Connect the existing tunnel

```powershell
Get-Service cloudflared
```

If a connector already runs on **this server**, open its tunnel in the Cloudflare dashboard and reuse it. Do not reinstall the connector, copy a token from another server or alter the accounting app's routes.

If this server has no connector, create a new Cloudflared tunnel for this server in Cloudflare, select Windows, and run its provided installation command **on this server**. The tunnel token is a secret; use it locally and keep it out of chat, Git and screenshots.

Add one published application route:

| Setting | Value |
| --- | --- |
| Subdomain | `stash` |
| Domain | `instrumentsofliberty.com` |
| Service type | HTTP |
| Service URL | `127.0.0.1:5240` |

Keep TLS termination at Cloudflare and loopback HTTP between cloudflared and the app. Do not open port 5240 in Windows Firewall or forward a router port. The tunnel and the app run on the same machine.

Cloudflare normally creates the associated DNS record. If a `stash` record already exists, inspect its use before replacing it. Leave other DNS records unchanged. Enable WebSockets, require HTTPS, and configure **Bypass cache** for this hostname. The app also sends `Cache-Control: no-store`.

## 5. Verify before using real documents

From another device (a phone on cellular data is useful):

1. Open `https://stash.instrumentsofliberty.com` in a private browser window. Access must challenge you before showing the workspace. An unauthorized email must be denied.
2. Sign in with an allowed account. Create a synthetic folder and upload a made-up text file under the initial `C:\Apps\DocumentStorage\storage` root. Search for its content, download it, rename it, trash it, and restore it. Verify buttons work; a rendered page alone does not confirm the Blazor WebSocket works.
3. Sign out of Access and confirm a new browser session cannot open the workspace or an original-file download.
4. Restart `DocumentStorage` and verify health and interactive behavior again. Verify automatic startup after a planned server restart when convenient.

Use the initial storage directory for synthetic testing. Before registering existing document directories, give LocalService Modify permission **only on those intended roots** and establish filesystem backups. Mapped drives from your interactive login are not available to a service. NAS access later needs a dedicated service identity with share and filesystem permissions.

## Updating

From the elevated server shell:

```powershell
Set-Location C:\Apps\DocumentStorage\repo
git pull --ff-only origin main
powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy\deploy.ps1
```

The update script builds a staging release first, makes and checks a PostgreSQL custom-format catalog backup, stops only DocumentStorage, retains the old binaries, switches, and verifies the exact new revision. On a startup failure it attempts binary rollback. Catalog backups are point-in-time database snapshots; they are not a backup of document bytes, and binary rollback does not undo migrations or restore a database. An incompatible migration needs deliberate recovery from the retained backup. No automatic in-app updater is implemented yet.

Common failures:

| Symptom | Check |
| --- | --- |
| Service exits immediately | Protected instance configuration, PostgreSQL access, Application event log |
| Public 401 after Access sign-in | Correct team domain and **this** application's AUD; server access to the HTTPS key endpoint and correct server clock |
| Cloudflare 502 | DocumentStorage service, local health, route's HTTP service and port 5240 |
| Cloudflare 1033 | This server's cloudflared connector and its outbound connection |
| Page loads but controls do nothing | WebSockets, Access coverage of `/_blazor`, cache bypass, browser errors |
| Local 401 | Expected for protected document routes; use the Access hostname. `/health` stays available. |
| Folder unavailable | LocalService's permissions and whether the storage is actually mounted |
