# Document Manager (working title)

A filesystem-first document workspace. Files stay in ordinary server folders; PostgreSQL holds the catalog, extracted text, folder guidance, and operation history. The display name is a placeholder, configurable through `Application:DisplayName`.

Built with .NET 10, Blazor Server, EF Core and PostgreSQL. The interface uses kraft cardboard for its frame and paper for the work: index-card filing slips, manila destinations, rubber-stamp statuses and sticky-note guidance. `crane550/2FAAccounts` is an infrastructure reference only; this project has its own code and database.

## Working features

The main loop is described in [Sorting](docs/sorting.md): the reader says what a document is, who it's from, its date and tags; Stash builds the folder and name from the kind's patterns; you approve with Enter, correct with a click, and let trusted kinds file themselves. See [the direction](docs/direction.md).

Working now:

- Searchable folder-tree dialogs with inline folder creation for Inbox destinations, moves, new-folder parents and rules, preserving the current editor.
- Prioritized Ollama servers/models, automatic fallback for sleeping computers, durable waiting when all readers are offline, and configurable idle model unloading.
- Strong-assistant MCP coaching: inspect Inbox/reader attempts, correct metadata and field guides, teach source-bound examples, and review before applying or verify afterward.

- Kinds, senders and tags (Kinds & tags page), details saved on every filed document and searchable (`kind:`, `from:`, `tag:`).
- An approval list in the Inbox with live destinations, new folders made as needed, Enter to file, batch approval, and a Filed list with Undo.
- Per-kind automatic filing once you switch it on, with a master switch; undoing one it filed switches its kind back to asking.
- Word (.docx) text extraction.
- Tidy up: documents re-file when a kind's patterns or a sender change (after you approve each move), and documents already in the Library can be read and given details.
- Arrange the name and folders with drag-and-drop chips on the slip, for one document or for every document of its kind.
- Fields on the spot: add a field on a document's slip, keep it for its kind and put it into the name or folder with a click; date fields are stored as dates so patterns can format them.
- Regulars: recurring documents from a sender you've approved a few times fill in from memory with no model call, and can be trusted to file themselves, even with the reader off.
- Assistant tools for kinds, senders, tags and details, with proposals you approve in the browser.
- Register existing local directories or mounted NAS shares; browse a nested folder tree.
- Discover existing files in place, rescan every minute or on demand, and retain indexed text when a root is offline.
- One **Library** for folders and files: a sidebar folder tree, breadcrumbs, folder tools in the header, rename/move with path previews, and folder notes.
- Create folders; upload files (100 MB each, 20 per selection); download originals; rename and move within a root.
- Recoverable trash and restore, preserving document/folder IDs and folder guidance for app-managed moves.
- In-app PDF viewer with page navigation, zoom, rotation, search, selectable text and password support; image and safe source-text previews in the Library and beside each Inbox filing slip.
- Search filenames, relative paths, UTF-8 text/Markdown/CSV, and text already embedded in PDFs (including ScanSnap OCR).
- Save independent document titles, summaries, reusable tags, organizations and discovered facts; search saved metadata and request folder suggestions without resending original OCR.
- Save plain-English folder instructions and inspect inherited instructions.
- Journal moves with content fingerprints before touching disk; verify moved files and complete folder trees, and recover verified interrupted moves on rescan.
- Dedicated **Recovery** workspace with actor-bound inspections, explicit catalog repair, preserved disk copies, transactional decision receipts and paused-storage indicators.
- Inbox folders, window-wide drag-and-drop and an always-present **Add to inbox** button with per-file results, stable-file readiness, and an **Inbox** where each document gets a filing slip beside its preview.
- Persisted, versioned filing rules (grouped by document type, made from a real document in one step) with trial runs and a live filename/destination on every slip.
- Explicit, fingerprint-checked **Confirm & file** through the existing recovery journal.
- Configurable local Ollama identification with durable jobs, cancellation/retries and document-type guidance.
- AI filing suggestions for unfamiliar categories: existing-folder selection, filenames, one new subfolder when needed, editable approval, and remembered corrections. No rule is required.
- Evidence-backed facts, optional automatic reading, explicit **Approve & file**, and removable filing examples.
- Instruction conversations, local summary drafts, source-linked immutable versions and manual editing.
- OAuth-authenticated Streamable HTTP MCP with scoped tools/resources/prompts, idempotent proposals, revocation, audit and individually approved filing.
- Ask indexed documents in persistent conversations, with validated evidence quotes and original/PDF-page previews.
- Conversational folder-tree planning with editable nested folders, reviewed creation and canonical filing instructions.
- Version/build details in the menu and administrator browser updates through an installed Windows task, with staged builds, required catalog backup and binary rollback.

See [Document questions and folder planning](docs/conversations.md), and [the agreed feature priorities](docs/feature-roadmap.md).

See [Browser updates and release numbers](docs/updating.md) for one-time updater installation and versioning.

See [The interface](docs/interface.md) for how the screens fit together, the filing loop and the design language.

See [Document previews](docs/previews.md) for formats, controls and preview verification.

See [Interrupted-operation recovery](docs/recovery.md) for inspections, decisions and verification limits.

See [Folder management](docs/folders.md) for organizing server directories.

See [Intake and deterministic filing](docs/intake-and-rules.md) for the filing workflow, and [Local document identification](docs/local-ai.md) to connect Ollama.

See [Instruction conversations and MCP](docs/assistants-and-mcp.md) for connection setup, permissions and protocol boundaries.

**Not implemented yet:** arbitrary filesystem/bulk MCP operations, per-folder client grants, permanent deletion, cross-root transfer/migration, and document backups. See [the product plan](docs/product-plan.md).

The app uses its own ASP.NET Identity username/password login, matching the accounting app's stack. Users and password hashes live in PostgreSQL; cookies are isolated per application/database. Cloudflare Tunnel routes traffic and supplies HTTPS. Cloudflare Access and Audience tags are not required. The origin always binds to loopback. See [Windows server and Cloudflare deployment](tools/deploy/README.md) for the separate service/database, protected instance configuration, first install, updates and tunnel setup. Production hosting is prepared; it still needs installation and verification on the actual server.

## Run

Prerequisites: .NET SDK 10.0.401 (later .NET 10 feature bands allowed), PostgreSQL 18, and Poppler's `pdftotext` on PATH. On Windows, set `Documents:PdfTextCommand` to the full executable path. Missing PDF tooling is shown as `PDF text tool unavailable`; filenames and other text still index. This build reads the first 100 PDF pages and retains up to 500,000 characters per file. Image-only PDFs are labeled `Needs OCR`; no OCR or AI is silently simulated.

Create an empty PostgreSQL database named `documentstorage_dev` using a role that owns it, then set the connection string through your shell/environment settings. Use a password appropriate for your server; never commit credentials.

```sh
# For the first startup, supply BootstrapAdmin__Username and BootstrapAdmin__Password
# through private environment settings (password minimum 8 characters). Existing accounts are never reset.
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

# Real OAuth consent/PKCE/rotation/revocation and official MCP client over HTTP:
dotnet run --project tests/DocumentStorage.McpChecks -c Release

# With the app running against a disposable catalog, system Chromium available:
npm --prefix tests/browser ci --ignore-scripts
node tests/browser/smoke.mjs
node tests/browser/sorting.mjs
# Drop checks require a fresh empty disposable catalog (default port 5241):
node tests/browser/drop.mjs
node tests/browser/folders.mjs # default local port 5242
# Recovery browser checks require the separate seeded fixture; see docs/recovery.md.
node tests/browser/instructions-mcp.mjs
```

The filesystem .NET runner refuses non-loopback databases or names that do not end `_checks`. Override its connection using `DOCUMENTSTORAGE_CHECKS_DB`. Browser checks sign in using `DOCUMENTSTORAGE_TEST_USERNAME`/`DOCUMENTSTORAGE_TEST_PASSWORD`, or the cloud fixture in ignored `.local/browser-credentials.json`. They create synthetic files under `.local/browser-storage` and register that root in the running catalog. They can be rerun and leave those sample files for inspection; never target a real catalog. Set `DOCUMENTSTORAGE_CHROMIUM` for your Chromium executable, `DOCUMENTSTORAGE_TEST_URL` for another local port, and `DOCUMENTSTORAGE_SCREENSHOT` for the screenshot output.

## Storage contract

Each root has a `.documentstorage-root` identity file. Preserve it when copying or reconnecting storage. An unavailable/mismatched marker means **offline**, not empty. Trash lives in `.documentstorage-trash` on the same root. Interrupted uploads may leave temporary files in `.documentstorage-staging`; these are never indexed as documents. These internal paths are hidden from the tree.

Run one application process per catalog. All mutations and scans share one writer gate; independent instances writing the same root are not supported. The service account needs read/write access to managed directories. Symlinks and Windows junctions are excluded and cannot be destinations. External users must not replace directories with links while an operation is running.

App-managed moves preserve IDs, history, and instructions. **External renames currently appear as a missing old record and a newly indexed record.** Stable identity across external moves needs the later fingerprint/reconciliation milestone. File system and PostgreSQL backups both matter; this version does not back them up automatically. NAS monitoring currently uses complete periodic scans rather than relying on network change notifications.

Search currently uses case-insensitive text matching with escaped query terms and pagination. PostgreSQL full-text ranking and indexes are planned as the catalog grows. AI identification is off by default. When enabled, text and bounded guidance go only to your configured Ollama server; cloud-backed models are refused before sending OCR. See [local AI boundaries](docs/local-ai.md). When using the public hostname, your browser traffic passes through your Cloudflare tunnel.
