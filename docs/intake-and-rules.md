# Intake and deterministic filing

This milestone makes a complete **review → rule → preview → file** workflow available without an AI connection. Optional [local Ollama identification](local-ai.md) now proposes types and facts into this same workflow, with explicit acceptance and human review. Human review and the filing engine are distinct; no document content is treated as instructions or executable code.

## Set up folders

Register a storage location as usual. Inside it, create two sibling folders such as `Incoming` and `Bank statements`. Open Incoming and choose **Use as intake** in its details. Multiple non-overlapping intake subfolders are supported. Files in their descendants enter the queue too. A storage root itself cannot be intake; keep a destination outside intake in the same storage location for this milestone.

A new or changed file needs two observations at least five seconds apart with unchanged size/timestamp before it becomes ready. An empty file or one held exclusively by a scanner remains pending. Stable files receive SHA-256 fingerprints; timestamps are normalized to PostgreSQL's microsecond precision. Use **Rescan storage** after the file settles, or wait for the existing one-minute background scan. Refresh Intake updates the displayed queue; it does not bypass readiness checks.

Offline storage keeps its queue entries and existing index. It cannot produce an executable filing preview. Intake remains a scope on existing files; marking/unmarking a folder does not move documents.

## Copy files from the browser

Drop individual files anywhere in the app window, or use **Choose files**, to copy them into the displayed **Intake destination**. The copy controls remain available across app views. A single intake folder is selected automatically; with several, choose a destination explicitly or open an intake folder in Documents. Offline destinations are unavailable. Removing a selected intake designation clears the selection rather than choosing somewhere else.

Original files stay on your computer. Each batch accepts up to 20 files, at most 100 MB each. Folder drops and batches exceeding these limits are rejected before copying. Existing names are never overwritten; a collision fails that file while other files in the batch continue. Per-file results show what succeeded and what needs retrying. Keep the browser page open until copying finishes. Successful copies appear in the catalog and intake queue; the usual stable-file checks still apply before identification or filing.

The server rechecks that the destination is still an available intake folder under the shared filesystem writer, then uses the existing staging and collision checks. There is no separate unauthenticated upload endpoint.

## Review a document

Open **Intake**, select a document, and read its indexed text or download the original. Folder guidance from its ancestors is available alongside the review.

Enter a document type and facts. Example:

| Field | Value |
| --- | --- |
| Document type | `bank_statement` |
| `institution` | `Cedar Bank` |
| `account_suffix` | `1234` |
| `statement_end` | `2026-10-31` |

Types/fact names use lowercase letters, numbers and underscores. Values remain text, preserving account suffixes with leading zeros. Blank UI rows are omitted. Up to 32 facts, 1,000 characters per value, are supported. `extension` and `original_stem` come from the actual file and cannot be overridden.

Check **I checked these facts against the document** and save. The review is bound to the file's current fingerprint. New contents require a fresh review, including changes that preserve size and timestamp. Review edits have revision checks to protect against conflicting tabs.

## Make and test a rule

In **Filing rules**, choose a descriptive name, matching document type, exact-match conditions and destination folder. Conditions are ANDed and ignore letter case. A rule without conditions matches all documents of its type.

A filename pattern could be:

```text
{statement_end:yyyy-MM-dd} - {institution} - {account_suffix}.{extension}
```

Facts in a pattern are required. Additional required facts may be listed separately. Dates must be ISO `yyyy-MM-dd` and support these output formats: `yyyy`, `yyyy-MM`, `yyyy-MM-dd`. Generated names must be valid portable filenames, preserve the original extension, and stay inside the chosen folder. Values containing path separators cause review rather than path traversal or silent cleanup.

Save a draft, then choose **Test against intake**. This evaluates the first 25 queue documents against that draft and the other enabled rules. It displays proposed names or blocking reasons, creates no executable preview tokens, and changes no files or operation history. Paused rules can be tested this way without enabling them.

Every save, including enabling/pausing, makes a new immutable version snapshot. History remains available in the editor. Concurrent edits must reload before saving; an old form never silently overwrites a newer version.

## Preview and file

Enable the intended rule, return to Intake, review the document, and choose **Save & preview**. A ready preview shows the precise rule version, filename and destination. Choose **Rename & file** to apply it.

Previews expire after 15 minutes and are bound to the requesting sign-in, source path/content, review revision, rule revision and destination path. Apply checks the current rules again, so a newly enabled conflicting rule also stops an old preview. Missing facts, an offline/missing destination, changed contents, expired/stale previews, duplicate names and competing rules stop filing. Nothing is overwritten.

The existing move journal records a filing operation before touching disk and links it to the approved preview. Catalog movement and preview completion commit together. A restart finishes an unambiguous interrupted operation; ambiguous state pauses writes for manual recovery. Retrying an already completed preview returns its recorded operation and does not move the document again. File IDs and bytes are retained. Once a document leaves intake, it leaves the queue while remaining searchable in the library.

The server services behind UI and MCP callers are `SetIntakeAsync`, `SaveReviewAsync`, `SaveRuleAsync`, `SimulateRuleAsync`, `PreviewFilingAsync`, and `ApplyFilingAsync`. Authenticated callers must supply the actual actor identity; token approval is checked against it. There are no new unauthenticated filesystem endpoints.

## Boundaries

- Enabling a rule makes it available to previews; **no unattended filing is enabled**.
- Facts can be entered manually or accepted from a separate Ollama proposal; human review is still required. Source-linked [instruction conversations](assistants-and-mcp.md) are available.
- Filing within one storage location uses the existing journaled move. Cross-location/NAS transfer needs staged copy, verification and recovery before it is enabled.
- Fingerprints validate intake contents. External rename identity reconciliation remains future work; an external rename still appears as missing old/new catalog records.
- Search remains the current text matching/index. Full-text ranking, PDF/image previews and bulk filing are separate milestones.

## Validation

`tests/DocumentStorage.Checks` covers readiness/locks, reviewed facts, immutable rules, dry runs, rule conflicts, stale/expired/actor-bound previews, collisions, changed bytes, destination renames, offline queues, restart recovery and idempotent filing with PostgreSQL and real temporary files.

`node tests/browser/intake.mjs` drives folder designation, upload/review, a draft simulation, activation/version history, exact preview, real filing, operation history and mobile layout. Like the other browser checks, run only against a disposable local catalog; it registers synthetic files under ignored `.local/intake-browser-storage` and writes `.local/intake-preview.png` and `.local/filing-rules.png`.

The additive `AddIntakeAndFilingRules` migration creates reviews, rule/version history and preview tables, and adds queue/fingerprint fields to catalog entries. Back up an existing server catalog before deploying. No folder is automatically designated as intake and no rule is automatically enabled by the migration.
