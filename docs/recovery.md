# Interrupted-operation recovery

A process restart, network interruption or filesystem error can leave disk and catalog disagreeing about a move. The app now has a **Recovery** workspace at `/recovery`; it uses the existing application login. Recovery requires no AI connection and is not exposed as an MCP mutation.

## Verification before and after a move

Before file/folder rename, move, trash, restore or deterministic filing, the Core service walks the source and saves its content/shape fingerprint with the prepared journal. Each file uses SHA-256. A folder digest includes relative names, file bytes and empty directories, independent of its absolute location and timestamps. Catalog path changes use a set-based SQL update inside the journal transaction; moving a large folder does not load all of its documents’ OCR into tracked entities. Unicode folder names and literal `%`/`_` path characters are covered by the filesystem checks.

The complete destination is checked again after the OS move, before catalog completion. A last-minute collision never overwrites the destination. A filesystem error or a different/unverifiable result leaves the operation paused for review; the app does not assume that an error means nothing moved.

Verification is streamed and bounded: **10,000 entries including the selected folder, 10 GiB of file bytes per location, 128 nested levels, and two minutes per snapshot**. The UI rounds the byte limit to 10 GB. A new operation exceeding those limits is refused before journaling or moving anything; organize smaller subfolders first. Symlinks/junctions, including dangling links and those inside the selected tree, are refused. The service cannot lock out unrelated programs for the entire folder transaction: avoid external edits/moves while organizing the same subtree. Disk and PostgreSQL backups remain necessary.

## What happens after an interruption

Scanning first inspects prepared operations. The root marker must identify the original connected storage. If only the original location exists and still matches the expected content/type, the untouched operation is cancelled. If only the destination exists, its contents are verified against the original fingerprint before applying the catalog move. Interrupted filing can also use its already-approved document fingerprint. These automatic decisions have receipts.

Both copies, both paths missing, mismatched content or an unverified older target-only journal require explicit review. An unavailable share never proves that files were deleted; its operation remains pending. A location with any `Prepared` or `Needs review` operation stays write-blocked. Its existing documents remain readable where their files are available. A banner identifies paused roots even if the operation has fallen outside Activity's latest-50 window. Other storage locations can continue working.

## Inspect and resolve

1. Open **Recovery** and select an unfinished operation. The queue is paged, 25 at a time. Inspection reads both physical locations and displays their relative paths, kind, counts, bytes and content digests, plus the saved original fingerprint when available.
2. Reconnect inaccessible storage before continuing. No decision is offered when either path cannot be completely verified as readable or missing. An operation beyond the verification limits cannot be resolved through this release's inspection tools; retain the files/journal and use a separately verified catalog recovery process rather than editing it blindly.
3. Choose the location that should retain the existing catalog IDs and guidance. Record a reason (10–1,000 characters), and confirm that you checked both paths and understand the identity choice.
4. Save the decision. The service hashes the paths and checks the catalog again under the existing writer. A changed file, folder layout, catalog binding, another recovery, an expired inspection or a different actor blocks a stale decision. An inspection lasts ten minutes.

**Keep original catalog location · cancel operation** retains original IDs/guidance at the source. Existing destination bytes stay untouched. A missing original retains its unavailable catalog identity; recovery does not fabricate files. Linked filing previews are cancelled and assistant proposals fail, so their old approval cannot be retried. Reindexing is forced, including when edited contents retain their previous size/timestamp.

**Use destination in catalog · complete operation** assigns original IDs/guidance to the verified destination. Any remaining source copy stays untouched. Destination type, parent, reserved catalog paths and intake nesting are checked before completion. A fingerprinted operation cannot adopt different contents. A filing fingerprint cannot be overridden by a human recovery decision.

Older ordinary operations did not record a content fingerprint. Automatic recovery no longer adopts their target-only state. Explicit adoption remains available with a clear lack-of-proof notice; you must establish that this is the intended item yourself. A malformed/unsupported saved fingerprint is not treated as permission for legacy adoption.

Both choices change only catalog/journal state. **Recovery never moves, deletes or overwrites disk copies.** Once all operations on a root are resolved, scanning discovers a remaining copy at a normal library path as a separate item with a new ID. It does not duplicate the original folder guidance onto that new identity. A copy inside an internal trash path stays preserved outside ordinary discovery; the audit/operation retains its relative location. Restore/trash decisions explain this distinction in the UI. Unavailable trash items keep their records and cannot be restored until the files are recovered.

## Durability and history

The decision receipt includes actor, time, reason, operation, paths, chosen action and outcome. The catalog move/cancellation, journal state, linked filing/proposal state and receipt commit in one PostgreSQL transaction. A failure between those saves rolls everything back. Retrying an already applied inspection with the same actor, decision and reason returns its original receipt, even if the inspection later expires; a different retry is rejected. A post-decision scan failure does not erase the committed receipt.

The UI shows the latest 50 decisions. Applied receipts and their inspected observations remain in PostgreSQL; unused inspections expire and are pruned after a day. Recovery does not erase the operation history or replace document backups. The additive `AddVerifiedOperationRecovery` migration preserves existing accounts, guidance, document identities and unresolved journal states; old operations start without an invented fingerprint. Previously closed Completed/Cancelled/Failed records remain closed; the migration does not reinterpret historical identities.

## Verification

The main `DocumentStorage.Checks` runner uses real PostgreSQL and synthetic temporary files. Recovery coverage includes full folder/empty-directory checks, interrupted trash/restore, both/missing/different copies, same-metadata content changes, stale catalog/actor/expiry, approval cancellation, idempotent receipts, offline markers, dangling links, reserved destination identities, an oversized sparse file, a collision after prepare, mutation after prepare and a failure between catalog and receipt saves. Authentication checks protect `/recovery`; the actual OAuth/MCP checks exercise journaled approved filing too.

The browser test requires a separate disposable catalog. Stop any app using it before resetting. From the repository directory:

```sh
# Destructive only to this disposable loopback *_recovery_checks catalog.
# Override with DOCUMENTSTORAGE_RECOVERY_FIXTURE_DB if necessary.
dotnet run --project tests/DocumentStorage.RecoveryFixture -c Release -- --reset

# In a separate shell start the app with this connection, private bootstrap admin settings,
# Development environment, loopback URL, and a key directory dedicated to this catalog:
# Host=127.0.0.1;Port=55432;Database=documentstorage_browser_recovery_checks;Username=postgres
# Urls=http://127.0.0.1:5243
# ASPNETCORE_ENVIRONMENT=Development
# Hosting__DataProtectionDirectory=<private catalog-specific keys directory>
# BootstrapAdmin__Username / BootstrapAdmin__Password supplied privately
ConnectionStrings__Catalog='Host=127.0.0.1;Port=55432;Database=documentstorage_browser_recovery_checks;Username=postgres' \
  Urls=http://127.0.0.1:5243 dotnet run --project src/DocumentStorage.Web -c Release --no-launch-profile

# Uses DOCUMENTSTORAGE_TEST_USERNAME/PASSWORD or the ignored cloud credential fixture:
node tests/browser/recovery.mjs
```

The standalone fixture refuses non-loopback databases, names without `_recovery_checks`, or invocation without `--reset`. It creates only synthetic storage under ignored `.local/`, writes local metadata there and adds no production fault-injection endpoints. Chromium checks real repairs, stale contents, disconnected identity markers, multiple operations, preserved copies/guidance, resumed folder writes, audit and mobile layout. They do not establish behavior of an actual Windows service, NAS outage or hardware failure; verify those on the server separately.
