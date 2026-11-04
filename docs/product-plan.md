# Product decisions and implementation plan

## Accepted direction

- Working name: **Document Manager**, a configurable placeholder. Do not choose a permanent name without the user.
- Purpose: organize ordinary files on local server disks, later a NAS, with both supported together. The database is a catalog, not the sole container for document bytes.
- The folder tree is central. Users manage real files and folders and select folders as filing destinations.
- ScanSnap drops OCR PDFs into an intake folder. Read the existing text before adding OCR machinery.
- Ollama handles slow document understanding and proposes names/destinations from real folder context without requiring a rule for every new category. Humans approve exact filing; deterministic rules remain optional for predictable repetitive work.
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

## Completed AI organizing milestone

The reader proposes a category, evidence-backed facts, filename and an existing destination, or one new subfolder. Corrections can be remembered as retrievable examples and forgotten in Settings. Exact actor-bound approvals, fingerprint/context checks, no-overwrite moves and transactional examples use the shared journal/recovery. Automatic reading queues suggestions only, one document at a time; unattended filing is not enabled. See [Local AI](local-ai.md). MCP retains its existing reviewed-facts/rule approval contract; external organizing proposals and learned-example tools remain future work.

## Completed instructions and MCP milestone

Source conversations, strict local guidance drafts/clarification, immutable accepted versions and source links are implemented. OAuth code/PKCE with registered clients, scoped official-SDK Streamable HTTP tools/resources/prompts, idempotent proposals, browser acceptance, connection/account revocation and exact approval-bound filing share the Core services and journal. See [the complete contract](assistants-and-mcp.md). Client grants cover the full library; arbitrary filesystem/bulk tools and per-folder authorization remain future work. Real external-account connections and Windows behavior still need verification.

## Completed operation recovery milestone

Ordinary file/folder moves, trash, restore and deterministic filing now save bounded content/shape fingerprints before touching disk, verify the result, and retain a paused journal when filesystem results are uncertain. The Recovery workspace inspects both locations, binds a decision to an actor and current disk/catalog state, records a reason/receipt transactionally, preserves both disk copies and reindexes after reconciliation. Verified single-sided results can recover automatically; older target-only journals need explicit adoption because their original bytes cannot be proven. See [Recovery](recovery.md) for limits and server verification.

## Completed preview milestone

Documents and Intake share a locally bundled PDF.js viewer with multipage scrolling, page navigation, zoom/fit, rotation, selectable text, search and browser-only PDF password entry. Native image formats and bounded escaped source-text previews share the expand/reload/download workflow. The existing cookie login protects range-enabled, version-bound preview streams. See [Previews](previews.md).

## Completed interface milestone

The workspace is organized around the filing loop: **Inbox** (documents, assistant suggestions and first-run setup), **Library** (folders, files and folder notes), **Rules** (document types and their rules), **History** (activity, trash, recovery) and **Settings** (storage, AI reader, assistants). A filing slip beside each inbox preview shows the destination live from the enabled rules; **Make a rule from this** drafts a rule from the document in front of you, for repeatable deterministic filing. AI approvals can separately retain corrected examples. Kraft cardboard frames the app and paper carries the work. See [The interface](interface.md).

## Completed browser update milestone

The menu footer reports immutable version/build/revision metadata. Settings › Updates checks the fixed application main branch and requests an exact, expiring release approval through an administrator-cookie/antiforgery boundary. A separately installed Windows task uses the existing staged build, verified catalog backup, service restart, health verification and binary rollback. Durable requests/status prevent duplicate deployments; the browser reloads across restart. See [Updates](updating.md). Actual task/ACL and service behavior requires server verification.

## Subsequent milestones

1. **Catalog hardening and search:** robust external rename reconciliation using fingerprints, extraction retry/backoff and PostgreSQL full-text search. Add bounded bulk filing after the per-document workflow is exercised.
2. **Guidance improvements:** evaluate real-model summary quality against labeled synthetic requests, add richer conversation navigation/comparison and guided source-to-rule compilation. Keep browsing/manual filing available without AI.
3. **MCP expansion and permissions:** per-folder/client grants and application user roles; bounded, approval-bound filesystem/bulk jobs with durable recovery and audit. Expand tools only after their idempotency and crash behavior is tested. Document text cannot grant permissions or change rules.
4. **Storage migration:** cross-root copy to staging, hash verification, durable journal, explicit commit/recovery, removal of source only after verified destination and catalog commit. Stable root/folder IDs survive an explicit root relocation. Offline shares pause affected work. Test partial copies, disk-full errors, restarts and source changes.
5. **Server release:** Built-in Identity login and Windows install/update scripts are prepared; validate their behavior on the actual server. Remaining work: server installation/rollback validation, application-level user roles, service-account NAS access, document backup/restore tooling. Never borrow accounting database names, passwords, ports or deployment targets.

A root with a `Prepared` or `Needs review` journal entry remains write-blocked until verified automatic recovery or an explicit recovery decision. Recovery changes catalog state without deleting or overwriting files. Cross-root transfers, document backup/restore, and recovery verification beyond the documented per-operation limits remain separate work.
