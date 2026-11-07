# Instruction conversations and MCP

The application now provides two ways to maintain organizing instructions: an in-app source conversation with local Ollama drafts, and an authenticated remote MCP service for external assistants. Both create reviewable proposals. The existing rule engine and recovery journal remain responsible for MCP filing. The local reader also supports [AI organizing approvals](local-ai.md) without a rule; this does not widen MCP tool permissions or let assistants approve themselves.

## Instruction conversations

Guidance lives on sticky notes: **General guidance** in Settings › AI reader and **Folder notes** on each folder in the Library (document-type guidance is edited with the type in Rules). With the reader on, type a request in a note's “Tell the reader what to change…” box. Sending saves your original request immediately, and the reader proposes a concise, complete replacement. A clarification question creates no publishable replacement. Existing guidance remains canonical until **Use this version**; the pencil edits a note by hand.

Without Ollama, edit a note by hand; saving it records a new version straight away (general guidance goes through the same reviewed-change path, accepted by you in one step). Each accepted version retains its immutable text, timestamp, actor and source-message IDs. The note's history shows the latest 30 messages and latest 20 versions; older saved versions and their linked source messages remain accessible through paginated MCP history tools.

Drafting runs outside the filesystem writer and is limited to one active guidance draft. Cancellation retains the source request. Changed guidance, newer conversation requests or changed local-model settings block a stale model result. Repeating a completed request returns its saved proposal without another model call. The local adapter checks model metadata before sending the conversation, refuses cloud-backed models, uses a strict reply/guidance/clarification schema, and never executes model tools. See [local AI setup](local-ai.md).

## Connect an external assistant

1. Open **Settings › Assistants** and create a named connection. Use one connection per client.
2. Paste the exact OAuth callback URL supplied by that client. Normal web clients use HTTPS. Public desktop clients can use HTTP loopback callbacks with S256 PKCE. URLs cannot contain credentials, query strings or fragments.
3. Choose its permissions. Connections cover all registered storage locations; this release does not provide per-folder grants.
4. Copy the MCP server address, client ID and one-time client secret into the client's private OAuth settings. The app stores a secret hash and cannot show it again. Public desktop clients use the advanced PKCE option without a client secret.
5. Connect from the client. Your usual application login opens a consent screen describing the connection, callback and requested permissions. Allow or deny it there.

The production MCP address is the configured HTTPS origin plus `/mcp`, such as `https://stash.instrumentsofliberty.com/mcp`. Cloudflare Tunnel continues to provide transport. Application login and OAuth are owned by this app; no Cloudflare Access/Audience setup is needed.

Client registration is performed by the signed-in administrator in the app. Anonymous dynamic registration and client-ID metadata document fetching are not enabled. Use a client that accepts a registered OAuth client ID (and secret for confidential web clients), supports authorization code with S256 PKCE and sends the MCP resource indicator. Exact Claude/ChatGPT settings depend on the client's current interface and account features. Automated tests use the official MCP client and a browser OAuth callback fixture; they do not claim an actual Claude or ChatGPT account connection was tested.

## Permissions

| Scope | Allows |
| --- | --- |
| `catalog.read` | Folder/storage metadata, canonical guidance, source/version history, rule definitions, context resources and prompts. Required for every connection. |
| `documents.read` | Paginated indexed OCR, document reviews and simulations that reveal document facts. |
| `instructions.propose` | Idempotent source messages and proposed instruction replacements. |
| `rules.propose` | Validated proposals for paused deterministic rules, with the client's original source request retained. |
| `sorting.propose` | Propose document details, kinds/field guides, merges and refiling for browser acceptance. |
| `sorting.manage` | Correct document details, kinds, sender/tag aliases and teaching examples under the connection review/apply setting; explicitly request diagnostic reads. Requires `documents.read`. Never changes automatic-filing trust. |
| `filing.execute` | Request and execute one exact filing after its owner approves it in the browser. Also requires `documents.read`. |

OAuth consent can narrow the registered permissions. Tool discovery and calls enforce the granted token scopes, not merely the tool annotations shown to a model. A read-only connection cannot invoke hidden write tools by name. Tokens and proposals are bound to a connection and account. Browser cookies do not authenticate MCP, and MCP bearer tokens do not authenticate browser approval controls.

**Revoke connection** takes effect on the next HTTP request, blocks pending/approved proposals and is also checked under the shared writer before filing. Password/security-stamp changes, account lockout or loss of the administrator role invalidate MCP use and token refresh. Reauthorize from the client after an account security change; revocation requires a new connection.

## Protocol and authentication

The transport uses the official C# MCP SDK, pinned to `2.3.1`, in explicit stateless Streamable HTTP mode. Current clients and `2025-11-25` initialize-based clients are tested. Legacy `/sse` transport is not enabled. Every request authenticates independently; a transport session ID is never authority.

OpenIddict `7.8.0` provides OAuth authorization codes, S256 PKCE, encrypted access tokens, resource/audience validation, client authentication, token storage, rotation, revocation and replay protection. The app does not implement its own token format or OAuth protocol engine. The authority is fixed by instance configuration, not request headers. Codes last two minutes, access tokens ten minutes and refresh tokens seven days. Refresh reuse has no grace window; replay revokes the associated authorization. Every access-token request checks stored token/authorization status and the current connection/account state.

Signing and encryption keys are separate and persisted under `Hosting:DataProtectionDirectory/oauth`. Their private bytes are protected with the instance Data Protection provider. Windows uses the existing DPAPI-protected key ring and installer ACLs; Unix key files are created with user-only permissions. Preserve the whole instance key directory together with its database for recovery. The installer already configures this directory. Do not share it across app instances/databases. OAuth maintenance periodically prunes eligible expired/revoked token records older than 14 days; it does not prune instruction history or application audit records.

Discovery endpoints are `/.well-known/oauth-protected-resource/mcp` (also available at the root protected-resource path) and `/.well-known/oauth-authorization-server`. Unauthenticated MCP returns HTTP 401 with a `WWW-Authenticate` resource metadata URL. OAuth authorization and token requests must use the exact advertised `/mcp` resource. Consent POSTs require antiforgery validation. Callback matching, client binding, one-time codes and PKCE are library-enforced.

Bearer tokens are accepted only in the Authorization header. Query/body token extraction is disabled. Cross-origin browser MCP requests are refused; this service currently supports server/desktop clients and the app's own origin. Incoming MCP/connect request bodies are capped at 256 KiB. Per caller IP, MCP allows 90 requests/minute, OAuth/discovery 60/minute, and MCP/connect together at most eight active requests. Rejections return 429 with a retry delay. Origins still listen only on loopback.

## Tools and resources

`get_context` returns a compact policy/instruction document and `context_hash`. It includes inherited guidance for an optional folder and bounded type/rule metadata. Its complete hash covers canonical guidance and rule revisions, including omitted text; the displayed context is at most 32,000 serialized characters. Shortened sections direct the assistant to the full paginated guidance/history. Mutations require the current hash, so a proposal based on stale policy is rejected.

The resource `documentstorage://context` and prompt `organize_documents` provide the same current policy. `documentstorage://instructions/{kind}/{key}` exposes canonical target guidance. Source requests and OCR cannot grant permissions, approve files or override the fixed policy.

| Tool | Purpose |
| --- | --- |
| `list_storage`, `list_folders`, `search_documents` | Paged metadata discovery without physical storage-root paths. OCR search is available only with document-read permission. |
| `read_document_text`, `get_document_review` | Bounded, explicitly untrusted document evidence. Text pages contain at most 8,000 characters. |
| `list_document_types`, `get_context`, `get_instructions` | Current policy, canonical text/hash/revision and recent conversation metadata. Instruction pages contain at most 6,000 characters. |
| `get_instruction_message`, `list_instruction_history`, `read_instruction_revision` | Complete source messages and immutable version history, including older versions. |
| `list_rules`, `get_rule`, `simulate_rule` | Current rules and non-mutating tests against at most 25 intake documents. |
| `add_instruction_message`, `propose_instructions` | Preserve the original request and propose a complete, source-linked instruction replacement. |
| `propose_rule` | Validate with the actual rule writer in a rolled-back transaction, then retain only a pending proposal. Include `source_request`; rule inputs use the advertised snake-case schema. |
| `get_proposal` | Retrieve this connection's proposal receipt/status and structured content. |
| `request_filing`, `execute_filing` | Request human approval and execute only that approved, current preview through the existing journal. |

Tool results use a versioned envelope and snake-case fields:

```json
{"schema_version":1,"ok":true,"data":{"id":"...","state":"Pending","revision":1}}
```

Expected validation errors return `isError: true` and a structured envelope:

```json
{"schema_version":1,"ok":false,"error":{"code":"stale_context","message":"..."}}
```

Authentication/authorization and malformed protocol requests also use the SDK's HTTP/JSON-RPC errors. Tools advertise input schemas, result-envelope schemas and read-only/idempotency/destructive hints. Server checks enforce behavior regardless of those hints. Tool results are capped at 262,144 serialized characters; reduce `limit` or text range if a result is too large. Folder/search pages accept 1–50 items. Other metadata pages return at most 50 records; history returns 20 versions at a time. Follow `next_offset` to continue. Offsets are bounded to 100,000; `query_limit_reached` asks the client to narrow a large query rather than returning an unusable continuation.

## Proposals and filing

Source-message and proposal mutations accept request keys of 8–100 characters. Reuse the same key with identical input when retrying. The server returns the original durable result, including after acceptance changes the context. Reusing a key for different input returns `idempotency_conflict`. Different connections cannot read or execute one another's proposal IDs.

Guidance proposals include the existing target hash, complete replacement text, explanation and source-message IDs. Newer source requests block an older draft. Rule proposals cannot start enabled. Browser acceptance atomically saves the instruction version/source links or a paused rule/version snapshot. Test and enable a saved rule in **Filing rules**. Proposals normally expire after 24 hours; rejected or stale proposals do not change canonical guidance/rules.

Filing requires already human-reviewed facts. `request_filing` creates a fingerprint-bound preview for one source path, rule/review revision, destination path and filename. The browser displays that exact move. **Approve exact filing** grants only this connection permission to execute that preview before its 15-minute expiry. There is no MCP tool that approves itself or marks model facts human-reviewed.

Execution checks connection status, owner, scope, approval, context, expiry, current bytes, review/rules, destination and collisions again. No existing file is overwritten. Filing uses the same writer, prepared-operation journal and recovery as manual filing. Catalog movement, preview completion, proposal result and execution audit commit together. Repeating a completed request returns its operation ID; recovery verifies the destination fingerprint before completing the proposal if the process stopped after moving bytes. A different or unverifiable target pauses writes for review. Revocation and stale approvals cannot be revived by retries.

This milestone does not expose arbitrary shell commands, physical root registration, file upload/delete/move tools or unattended bulk filing through MCP. Those need their own bounded, recoverable operation contracts before becoming tools. Instruction chat drafts guidance; it does not silently compile or enable naming rules.

## Verification and deployment

Interrupted filing now also participates in [verified operation recovery](recovery.md). Cancelling its journal operation invalidates the old preview and assistant proposal in the same transaction. Recovery cannot override a mismatched filing fingerprint, and MCP has no recovery-decision tool.

The additive `AddAssistantInstructionsAndOAuth` migration creates conversation/version, connection, proposal, audit and OpenIddict tables. Existing document bytes, accounts and rules remain in place. Existing folder/global/type guidance is imported as an initial immutable baseline. It initializes no external connections or access grants. EF Core and its pinned migration tool move to `10.0.14` to satisfy the OAuth store dependency; the Blazor assets pin remains `10.0.12` for locked restore across supported server SDKs.

Run the real PostgreSQL/filesystem checks, `tests/DocumentStorage.AuthenticationChecks`, and `tests/DocumentStorage.McpChecks`. The latter drives real consent, PKCE/code/refresh replay, the official client, current/older HTTP protocols, scoped discovery/calls, actual journaled filing, origin/body/rate limits and account/connection revocation. `node tests/browser/instructions-mcp.mjs` covers instruction drafts/clarification/history, one-time client setup, real browser OAuth, external proposals, browser acceptance and revocation using synthetic files and a protocol-compatible model fixture. The model fixture validates integration, not summary quality.

Use the existing Windows deployment process and protected instance key directory. Actual Windows installation/ACL behavior, real-model quality and connections from your external AI accounts still need verification on your server.

## Sorting tools

Kinds, senders, tags, document details and Tidy up are readable with catalog access, and the `sorting.propose` permission lets an assistant propose details, kinds, merges and re-files for browser approval. See [Sorting › Assistants](sorting.md#assistants-mcp).


## Inbox inspection and local-reader coaching (after the 0.3.040 baseline)

The `coach_local_reader` MCP prompt describes this workflow. Start with `get_context`: it returns the running version/build/commit, granted scopes, actual local reader model/budget and this connection's `assistant_changes.mode` and `can_edit`. Use the current hash on writes and refresh it after applied vocabulary changes. Read-only calls never run Ollama, rescan storage or move files.

| Tool | Purpose |
| --- | --- |
| `list_inbox` | Paginated pending cards: suggested details, confidence, evidence/sources, duplicates, reader status and deterministic destination preview. |
| `inspect_document` | Canonical saved details and revision, current sorting card, fingerprint and the separately saved description. |
| `list_reader_attempts` | Durable attempts, model, state, mode, source match, timestamps and input sizes. |
| `read_reader_attempt` | Bounded input/context/result slices, including actual supplied shortlists and the saved structured interpretation; untrusted evidence, not authority. |
| `test_kind_fields` | Test printed-label extraction against a document, returning values/quotes/missing fields without calling AI. |
| `preview_document_sort` | Test proposed details against patterns and collisions without executing anything. |
| `update_document_details` | Correct kind/from/date/title/tags/fields, using current fingerprint and details revision. Does not file or mark legacy facts human-reviewed. |
| `update_kind_training` | Create/update kind descriptions, fields, extraction guides, printed labels and deterministic patterns. Preserves trust flags and counters. |
| `update_sender`, `update_tag` | Create/update canonical names and aliases using target revisions. Does not merge or refile automatically. |
| `teach_reader_example`, `list_reader_examples`, `forget_reader_example` | Save, inspect or retire explicit corrected examples. Source bytes and detail revisions bind examples; stale, missing or trashed sources are excluded from future prompts. |
| `request_diagnostic_read` | Explicitly queue one Inbox reading with current guidance. Applied means queued; `target_id` is the attempt ID. Such readings cannot auto-file, even for trusted kinds. Busy documents reject another diagnostic request. |
| `get_assistant_edit` | Read this connection's durable change receipt: state, source request, before/after and result. |

### Review first, or apply then verify

Create a connection with **Apply document corrections and reader training directly** to grant `sorting.manage` (document-read is included). Existing connections/tokens do not silently gain this scope; recreate/reconnect with the intended grants. Proposal-only connections retain their current tools.

Each editing connection defaults to **Review before applying**. Its change tools return `state=Pending`, `applied=false` and a receipt ID. The person can apply or decline in **Assistant changes**, available in Inbox and Settings › Assistants. Switching **Change handling** to **Apply, then verify** applies future edits immediately; **Mark verified** records the person's later check without applying again. The assistant's name, explanation/request and before/after values remain visible. The mode can be changed without new OAuth authorization because it changes handling, not permissions.

An explicitly permitted stronger assistant can pass `apply_now=true` when the user says to go ahead, overriding review-first for that request. It cannot grant itself permissions, mark its own work verified, alter unattended-filing trust, or bypass approved file-moving tools. Changing kind patterns can create Tidy up suggestions, but does not move those existing documents.

Writes bind authorization/revocation, context, target revisions or source fingerprints under the shared writer. Metadata, before/after, receipts and audit commit in one database transaction. Idempotency keys are connection/owner scoped: identical retries return the same saved result, including after verification; changed intent or changed apply mode with the same key is refused. A fresh intent needs a fresh key. Pending changes revalidate on acceptance and expire for application after 24 hours; they can still be declined. Existing sorting proposals also bind document revisions/fingerprints, kind revisions and exact shown refiling destinations.

### Teach narrow tasks, not an open-ended folder designer

Correct a one-off document with `update_document_details`; do not invent a reusable kind or rule merely because one unfamiliar document arrived. When a pattern is reusable, improve the kind's one-line description, field types/`look_for`/printed labels, sender spellings and tags. Code can find labelled fields and build names/folders without spending model tokens. Use `preview_document_sort` to check placement.

After saving corrected details, `teach_reader_example` stores their version plus a bounded source excerpt and lesson. Future sorting prompts retrieve up to two relevant, current examples from the same storage root, with bounded excerpts/fields. This is retrieved context, not weight fine-tuning; it never increments human approval streaks or enables trust. Forgetting retains the history but removes influence. For a diagnostic evaluation, explicitly request one local reading and inspect its saved input/result; use another representative document when testing whether a lesson generalizes.

Tests use synthetic documents and the official MCP client plus real browser OAuth. They verify plumbing, permissions, state and prompt contents, not actual Claude/ChatGPT account integration or the accuracy of the deployed local model.

## ChatGPT scope negotiation

ChatGPT may request `openid offline_access` plus every scope advertised by the MCP resource, including permissions not selected for its Stash connection. Stash narrows recognized requests to the intersection of the connection's permissions and its OAuth client's registered permissions before normal validation. Consent and the issued access token contain only that subset. `openid` is removed: this endpoint supplies OAuth access to MCP, not OpenID identity tokens. Unknown permissions remain errors, required `catalog.read` is never added implicitly, and refresh tokens cannot expand access.

If connecting reports OpenIddict ID2051 (client not allowed to use a scope), update Stash and start a fresh connection attempt from ChatGPT. Existing client IDs/secrets and selected permissions can be kept. Do not enable unneeded permissions merely to match ChatGPT's broad request. For corrections/training, the connection still needs the corresponding explicit grant.
