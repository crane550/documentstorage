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
- Login follows the other applications: app-owned ASP.NET Identity users/passwords and cookies. Cloudflare Tunnel provides the route; Access/Audience is not required.
- Infrastructure blueprint: .NET/Blazor/PostgreSQL, per-instance configuration, server installation and recoverable updates from 2FA Accounts. No accounting code or production resources are shared.
- Theme: ivory `#F5F1E8`, paper `#FFFCF5`, kraft `#D8BE96`, brown `#654A32`, charcoal `#302B25`, sage `#DEE5D5`, forest `#446348`, amber `#A56316`, brick `#A23E35`. Folder tabs/paper labels, restrained texture, accessible focus and contrast.

## Layers

`DocumentStorage.Data` owns the catalog, roots, stable entry IDs and operation journal. EF migrations version the PostgreSQL schema.

`DocumentStorage.Core` owns path validation, discovery, extraction, operation recovery and deterministic rule evaluation. No UI types. MCP tools call these services rather than executing shell commands.

`DocumentStorage.Web` owns the Blazor workspace, guidance editor and composition. Loopback origins, with ASP.NET Identity users/passwords, isolated cookies and app-owned login for tunnel hosting. Windows install/update tooling is in tools/deploy; actual server validation remains necessary.

`tests/DocumentStorage.Checks` exercises real PostgreSQL and temporary filesystem operations. `tests/browser` drives the actual UI with synthetic files.

## Completed intake milestone

Folder-scoped intake, stable observations and fingerprints, manual reviewed facts, persisted/versioned rules, draft simulations, and explicit preview-bound filing are implemented. See [the workflow](intake-and-rules.md). Rule activation does not enable unattended filing.

## Completed local identification milestone

Configurable Ollama connection/model, document-type guidance, bounded JSON context, durable queue/retry/cancellation/restart recovery, and evidence-backed proposals are implemented. Optional automatic identification never approves or files documents. See [Local document identification](local-ai.md). Automated protocol/UI checks use a synthetic fixture; real-model accuracy and the Windows server connection still need verification.

## Completed instructions and MCP milestone

Source conversations, strict local guidance drafts/clarification, immutable accepted versions and source links are implemented. OAuth code/PKCE with registered clients, scoped official-SDK Streamable HTTP tools/resources/prompts, idempotent proposals, browser acceptance, connection/account revocation and exact approval-bound filing share the Core services and journal. See [the complete contract](assistants-and-mcp.md). Client grants cover the full library; arbitrary filesystem/bulk tools and per-folder authorization remain future work. Real external-account connections and Windows behavior still need verification.

## Subsequent milestones

1. **Catalog and document previews:** robust external rename reconciliation using fingerprints, extraction retry/backoff, PostgreSQL full-text search, and PDF/image preview. Add bounded bulk filing after the per-document workflow is exercised.
2. **Guidance improvements:** evaluate real-model summary quality against labeled synthetic requests, add richer conversation navigation/comparison and guided source-to-rule compilation. Keep browsing/manual filing available without AI.
3. **MCP expansion and permissions:** per-folder/client grants and application user roles; bounded, approval-bound filesystem/bulk jobs with durable recovery and audit. Expand tools only after their idempotency and crash behavior is tested. Document text cannot grant permissions or change rules.
4. **Storage migration:** cross-root copy to staging, hash verification, durable journal, explicit commit/recovery, removal of source only after verified destination and catalog commit. Stable root/folder IDs survive an explicit root relocation. Offline shares pause affected work. Test partial copies, disk-full errors, restarts and source changes.
5. **Server release:** Built-in Identity login and Windows install/update scripts are prepared; validate their behavior on the actual server. Remaining work: server installation/rollback validation, application-level user roles, service-account NAS access, document backup/restore tooling and an in-app updater. Never borrow accounting database names, passwords, ports or deployment targets.

A root with a `Needs review` journal entry is write-blocked in this build. A maintenance recovery UI is required before release: inspect both paths and byte identity, record an explicit resolution, and then allow reconciliation. Do not auto-clear ambiguity or overwrite destination documents.
