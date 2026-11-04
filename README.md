# Document Manager (working title)

A filesystem-first document workspace. Files stay in ordinary server folders; PostgreSQL holds the catalog, extracted text, folder guidance, and operation history. The display name is a placeholder, configurable through `Application:DisplayName`.

Built with .NET 10, Blazor Server, EF Core and PostgreSQL. The interface uses warm paper, kraft cardboard, brown, and sage. `crane550/2FAAccounts` is an infrastructure reference only; this project has its own code and database.

## First milestone

Working now:

- Register existing local directories or mounted NAS shares; browse a nested folder tree.
- Discover existing files in place, rescan every minute or on demand, and retain indexed text when a root is offline.
- Create folders; upload files (100 MB each, 20 per selection); download originals; rename and move within a root.
- Recoverable trash and restore, preserving document/folder IDs and folder guidance for app-managed moves.
- Search filenames, relative paths, UTF-8 text/Markdown/CSV, and text already embedded in PDFs (including ScanSnap OCR).
- Save plain-English folder instructions and inspect inherited instructions.
- Journal moves before touching disk; recover unambiguous interrupted moves on rescan and pause ambiguous operations for review.
- A tested, data-only filing-rule preview contract. It never calls an AI or moves files on its own.

**Not implemented yet:** Ollama identification, automatic intake/filing, instruction chat, rule editing/activation, MCP authentication/tools, PDF/image preview, permanent deletion, cross-root transfer/migration, self-update, and document backups. See [the product plan](docs/product-plan.md).

The app uses its own ASP.NET Identity username/password login, matching the accounting app's stack. Users and password hashes live in PostgreSQL; cookies are isolated per application/database. Cloudflare Tunnel routes traffic and supplies HTTPS. Cloudflare Access and Audience tags are not required. The origin always binds to loopback. See [Windows server and Cloudflare deployment](tools/deploy/README.md) for the separate service/database, protected instance configuration, first install, updates and tunnel setup. Production hosting is prepared; it still needs installation and verification on the actual server.

## Run

Prerequisites: .NET SDK 10.0.401 (later .NET 10 feature bands allowed), PostgreSQL 18, and Poppler's `pdftotext` on PATH. On Windows, set `Documents:PdfTextCommand` to the full executable path. Missing PDF tooling is shown as `PDF text tool unavailable`; filenames and other text still index. This build reads the first 100 PDF pages and retains up to 500,000 characters per file. Image-only PDFs are labeled `Needs OCR`; no OCR or AI is silently simulated.

Create an empty PostgreSQL database named `documentstorage_dev` using a role that owns it, then set the connection string through your shell/environment settings. Use a password appropriate for your server; never commit credentials.

```sh
# For the first startup, supply BootstrapAdmin__Username and BootstrapAdmin__Password
# through private environment settings (password minimum 12 characters). Existing accounts are never reset.
export ConnectionStrings__Catalog='Host=127.0.0.1;Port=55432;Database=documentstorage_dev;Username=postgres'
dotnet tool restore --tool-manifest dotnet-tools.json
dotnet restore --locked-mode
dotnet build DocumentStorage.sln -c Release --no-restore
dotnet run --project src/DocumentStorage.Web -c Release --no-build --no-launch-profile
```

First startup creates the administrator from private bootstrap settings; subsequent starts use the existing account. Sign in at `/login`, and change your password/sign out under `/account`. Five failed attempts lock an account for 15 minutes. The local host defaults to port 5240. `GET /health` verifies the database connection. EF migrations apply on startup. Connect an **existing** empty/synthetic directory using **Add storage**. Real document content and database dumps do not belong in this repository.

An optional absolute `DOCUMENTSTORAGE_INSTANCE` path loads per-instance JSON. It is followed by environment overrides. Keep database details, display name, bind URLs, and PDF tool location there; a sample is in [tools/instance.example.json](tools/instance.example.json). `instance.json`, `.local/`, build outputs, and per-machine configuration are ignored.

## Verify

```sh
# Destructive only to its dedicated loopback *_checks database; uses synthetic temporary files.
dotnet run --project tests/DocumentStorage.Checks -c Release

# Real application pipeline with app login/cookies, password changes and lockout and its own *_checks database:
dotnet run --project tests/DocumentStorage.AuthenticationChecks -c Release

# With the app running against a disposable catalog, system Chromium available:
npm --prefix tests/browser ci --ignore-scripts
node tests/browser/smoke.mjs
```

The filesystem .NET runner refuses non-loopback databases or names that do not end `_checks`. Override its connection using `DOCUMENTSTORAGE_CHECKS_DB`. Browser checks sign in using `DOCUMENTSTORAGE_TEST_USERNAME`/`DOCUMENTSTORAGE_TEST_PASSWORD`, or the cloud fixture in ignored `.local/browser-credentials.json`. They create synthetic files under `.local/browser-storage` and register that root in the running catalog. They can be rerun and leave those sample files for inspection; never target a real catalog. Set `DOCUMENTSTORAGE_CHROMIUM` for your Chromium executable, `DOCUMENTSTORAGE_TEST_URL` for another local port, and `DOCUMENTSTORAGE_SCREENSHOT` for the screenshot output.

## Storage contract

Each root has a `.documentstorage-root` identity file. Preserve it when copying or reconnecting storage. An unavailable/mismatched marker means **offline**, not empty. Trash lives in `.documentstorage-trash` on the same root. Interrupted uploads may leave temporary files in `.documentstorage-staging`; these are never indexed as documents. These internal paths are hidden from the tree.

Run one application process per catalog. All mutations and scans share one writer gate; independent instances writing the same root are not supported. The service account needs read/write access to managed directories. Symlinks and Windows junctions are excluded and cannot be destinations. External users must not replace directories with links while an operation is running.

App-managed moves preserve IDs, history, and instructions. **External renames currently appear as a missing old record and a newly indexed record.** Stable identity across external moves needs the later fingerprint/reconciliation milestone. File system and PostgreSQL backups both matter; this version does not back them up automatically. NAS monitoring currently uses complete periodic scans rather than relying on network change notifications.

Search currently uses case-insensitive text matching with escaped query terms and pagination. PostgreSQL full-text ranking and indexes are planned as the catalog grows. No document text is sent to an external AI. When using the public hostname, your browser traffic passes through your Cloudflare tunnel.
