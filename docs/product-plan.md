# Product decisions and implementation plan

## Accepted direction

- Working name: **Document Manager**, a configurable placeholder. Do not choose a permanent name without the user.
- Purpose: organize ordinary files on local server disks, later a NAS, with both supported together. The database is a catalog, not the sole container for document bytes.
- The folder tree is central. Users manage real files and folders and select folders as filing destinations.
- ScanSnap drops OCR PDFs into an intake folder. Read the existing text before adding OCR machinery.
- Ollama handles slow document identification. It supplies facts; deterministic validated rules choose names and destinations.
- Plain-English conversation creates/updates folder and document-type guidance and proposes structured rules. Keep readable source guidance alongside the compiled rule representation.
- External Claude/ChatGPT assistants use a robust authenticated MCP backend. All UI and MCP mutations use the same application services.
- Compact context should include global policy, relevant inherited guidance, schemas and matching rules. Retrieved document text is data, never instructions or authority to perform operations.
- Infrastructure blueprint: .NET/Blazor/PostgreSQL, per-instance configuration, server installation and recoverable updates from 2FA Accounts. No accounting code or production resources are shared.
- Theme: ivory `#F5F1E8`, paper `#FFFCF5`, kraft `#D8BE96`, brown `#654A32`, charcoal `#302B25`, sage `#DEE5D5`, forest `#446348`, amber `#A56316`, brick `#A23E35`. Folder tabs/paper labels, restrained texture, accessible focus and contrast.

## Layers

`DocumentStorage.Data` owns the catalog, roots, stable entry IDs and operation journal. EF migrations version the PostgreSQL schema.

`DocumentStorage.Core` owns path validation, discovery, extraction, operation recovery and deterministic rule evaluation. No UI types. A future MCP tool calls these services rather than executing shell commands.

`DocumentStorage.Web` owns the Blazor workspace, guidance editor and composition. Local-only until authentication and authorization are available.

`tests/DocumentStorage.Checks` exercises real PostgreSQL and temporary filesystem operations. `tests/browser` drives the actual UI with synthetic files.

## Subsequent milestones

1. **Intake and rule workspace:** choose intake folders, track stable-file readiness, document facts and review states; persist/version rules; show a dry-run plan including affected filenames and collision checks before activation. Add file fingerprints, robust external rename reconciliation, text extraction retries and PostgreSQL full-text search. Add preview for PDFs/images.
2. **Ollama and guidance conversation:** configurable local endpoint/model, queued work with backoff/cancellation, structured fact schema, uncertainty/review, document-type instructions, conversation history and source-to-rule change tracking. Do not make AI availability a prerequisite for browsing or manual filing.
3. **MCP and permissions:** authenticated OAuth transport, scoped read vs propose vs approved execution, compact resource context, rule simulations, approval-bound operations, bounded bulk jobs and audit history. Authorization and action validation happen server-side. Document text cannot grant permissions or change rules.
4. **Storage migration:** cross-root copy to staging, hash verification, durable journal, explicit commit/recovery, removal of source only after verified destination and catalog commit. Stable root/folder IDs survive an explicit root relocation. Offline shares pause affected work. Test partial copies, disk-full errors, restarts and source changes.
5. **Server release:** authentication before LAN hosting; service-account NAS access, protected per-instance configuration and keys, Windows service install, database/files backup, staging builds, update/restart health check and rollback. Never borrow accounting database names, passwords, ports or deployment targets.

A root with a `Needs review` journal entry is write-blocked in this build. A maintenance recovery UI is required before release: inspect both paths and byte identity, record an explicit resolution, and then allow reconciliation. Do not auto-clear ambiguity or overwrite destination documents.
