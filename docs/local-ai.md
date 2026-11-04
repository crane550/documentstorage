# Local document identification

Ollama reads indexed OCR text and proposes a document type and facts. The deterministic filing engine still controls names and destinations. A proposal does not save a review or move a file.

## Connect your model

Open **Settings › AI reader**. Enter the direct address of your own Ollama server, normally `http://127.0.0.1:11434` when it runs on the same computer as the application. The address is resolved from the application server, not your browser. A local network Ollama machine is supported too. Use the root HTTP/HTTPS address without credentials, query strings or a path.

Choose **Check connection & list models**, then select an installed chat model. This sends no document text and does not download a model. Model choice depends on your machine and extraction quality; the app does not select or install one automatically. Model listing excludes cloud-backed entries. Each identification checks `/api/show` before sending OCR to `/api/chat` and refuses models reporting an upstream host/model. HTTP redirects are disabled; normal HTTPS certificate verification remains enabled. Use a direct Ollama endpoint you control.

Save with **Use the AI reader** switched on. Identification and automatic identification are both off on a fresh catalog. The default request limit is ten minutes. You can allow 30–1,800 seconds for slower hardware. A temporary connection/server error retries after 30 seconds and then 120 seconds, up to three model attempts. Invalid structured responses fail without repeatedly asking the model. You can explicitly identify again after fixing a problem.

## Give it context

General identification guidance, inherited folder instructions, document-type guidance and hints from enabled filing rules are compiled into a compact, versioned JSON context for each job. Document types have a stable identifier, a readable name, recognition instructions and fact names. Edit these in **Rules**: select a type and choose **Edit type**. For example:

> A bank statement describes an account over a statement period. Use the period's closing date, not the download or print date. Extract the institution and last four account digits. Leave missing facts out.

Use fact names such as `institution`, `account_suffix` and `statement_end`. Dates should be unambiguous `yyyy-MM-dd`. Types and fact names from enabled rules are included, so identification can supply the facts those rules need. File-operation fields such as `destination` and `filename` are not model facts.

The prompt context is bounded to 16,000 serialized characters, up to 31 configured types plus `unknown` and 64 fact names. Longer guidance/rule sets are shortened and flagged for review. The OCR budget defaults to 12,000 characters and can be set to 2,000–16,000. Longer documents use beginning and ending excerpts with an explicit omission marker. The original indexed text is retained. Changes even to omitted guidance invalidate old work.

Instruction conversations, immutable source-linked versions and external MCP resources/tools are now available. See [Instruction conversations and MCP](assistants-and-mcp.md). The same enabled local model can draft guidance; its response remains a proposal for review.

## Review a proposal

1. Make a subfolder an inbox and let the scan settle and index.
2. Open the document in the **Inbox** and choose **Read with AI** on its filing slip.
3. Continue using the library while the job runs. The selected document's status refreshes periodically. **Settings › AI reader** shows recent jobs and provides cancellation and retry controls.
4. Inspect the proposed type, summary, facts and OCR evidence. Uncertain/shortened inputs are flagged. Unsupported evidence is omitted. Evidence supports review; it does not prove the model's interpretation or normalized fact is correct.
5. Choose **Use suggestion** to replace the slip's fields. Nothing is confirmed by doing so. You can edit the fields yourself.
6. Check the facts against the original, then choose **Confirm & file** (or **Confirm facts**). The existing rule engine must produce a valid preview before **Rename & file** is available.

**Read new inbox documents automatically** queues ready, indexed, unreviewed intake files in rotating batches. It only creates proposals. It does not overwrite manual reviews, approve facts or enable unattended filing. Cancelled/failed jobs are not repeatedly requeued with identical source, review and guidance; use an explicit retry. Changed inputs can produce a new job.

## Durability and boundaries

Jobs are stored in PostgreSQL with their source fingerprint/path, model, context snapshot, settings/review revisions and attempt count. The app runs one model job at a time and releases the filesystem writer during inference. Startup recovers interrupted work without resetting the three-attempt limit. Unavailable storage waits until it can be revalidated.

Changing local-model settings cancels pending/running work. Cancellation also signals the active HTTP request; a late result cannot revive the job. Source changes, moves, manual-review edits or guidance changes discard stale responses. Loading a successful proposal into the editor checks its bindings and source bytes again. Reviewed facts and the filing journal remain separate from identification jobs.

For identification, only indexed text, the filename and bounded context are sent to the configured Ollama server. Guidance drafting instead sends canonical instructions and recent saved conversation messages. No document binary or absolute server path is included. Model output is parsed against a strict schema and displayed as escaped text. OCR is explicitly untrusted evidence and cannot authorize operations. The adapter never executes model tools, changes rules or chooses destination paths.

The additive `AddLocalIdentification` migration creates settings, document-type and durable-job tables. Existing documents and filing rules are preserved. Production use still needs a real installed Ollama model and extraction-quality checks on your hardware. Automated verification uses synthetic documents with a protocol-compatible test fixture, not a real language model or a claim about model accuracy.

## Verification

`tests/DocumentStorage.Checks` includes Ollama protocol/schema checks and real PostgreSQL/filesystem tests for queueing, retries, cancellation, restart recovery, guidance/review/content changes and human-review boundaries. `node tests/browser/ollama.mjs` starts a loopback Ollama protocol fixture and drives the real login, settings, guidance, background worker and intake UI. It uses only synthetic files and disables identification before shutting down the fixture. Run against a disposable local catalog, never real documents.
