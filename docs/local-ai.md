# Local document identification

Ollama reads indexed OCR text and proposes a category, useful evidence-backed facts, filename and destination. It can recognize unfamiliar categories without a configured type or rule. You approve an exact filing plan before the Core service moves anything through the recovery journal. Deterministic rules remain available for repeatable naming.

## Connect your model

Open **Settings › AI reader**. Enter the direct address of your own Ollama server, normally `http://127.0.0.1:11434` when it runs on the same computer as the application. The address is resolved from the application server, not your browser. A local network Ollama machine is supported too. Use the root HTTP/HTTPS address without credentials, query strings or a path.

Choose **Check connection & list models**, then select an installed chat model. This sends no document text and does not download a model. Model choice depends on your machine and extraction quality; the app does not select or install one automatically. Model listing excludes cloud-backed entries. Each identification checks `/api/show` before sending OCR to `/api/chat` and refuses models reporting an upstream host/model. HTTP redirects are disabled; normal HTTPS certificate verification remains enabled. Use a direct Ollama endpoint you control.

Save with **Use the AI reader** switched on. Identification and automatic identification are both off on a fresh catalog. The default request limit is ten minutes. You can allow 30–1,800 seconds for slower hardware. A temporary connection/server error retries after 30 seconds and then 120 seconds, up to three model attempts. Invalid structured responses fail without repeatedly asking the model. You can explicitly identify again after fixing a problem.

## Give it context

The reader receives general guidance, inherited source/destination folder notes, real eligible folder IDs and names in the same storage location, and up to five relevant remembered filing examples. It prefers an existing folder, even with imperfect spelling. If none fits, it may propose one new child folder beneath an existing destination. Inbox folders and all their descendants are excluded as destinations. No absolute server paths are sent.

The destination inventory is ranked by words in the filename, text and guidance. At most 120 folders and 16,000 serialized characters of folder metadata are included; destination guidance is shortened to 500 characters per folder, source guidance to 2,000, and global guidance is limited to 4,000. If some folders are omitted, the model cannot propose a new folder; you can select from the full list yourself. This is bounded retrieval, so a large library may need better folder notes to surface the right destination. All folder guidance, including omitted text, contributes to stale-context validation.

The reader may propose up to eight useful facts using lowercase identifiers, with quoted source evidence. Dates must remain unambiguous; an ambiguous revision/date token should be preserved rather than turned into an invented date. File operations are separate structured proposal fields, never facts or executable commands. The OCR budget defaults to 12,000 characters and can be set to 2,000–16,000. Longer documents use beginning and ending excerpts with an explicit omission marker. The original indexed text is retained.

For example, a ministry post guide can go directly to your existing church safety folder with a meaningful title and revision. No `ministry_post_guide` profile or filing rule needs to exist first. The output still depends on the installed model and quality of extracted text.

Instruction conversations, immutable source-linked versions and external MCP resources/tools are now available. See [Instruction conversations and MCP](assistants-and-mcp.md). The same enabled local model can draft guidance; its response remains a proposal for review.

## Review a proposal

1. Make a subfolder an inbox and let the scan settle and index.
2. Open the document in the **Inbox** and choose **Read with AI** on its filing slip.
3. Continue using the library while the job runs. The selected document's status refreshes periodically. **Settings › AI reader** shows recent jobs and provides cancellation and retry controls.
4. Inspect the summary, suggested filename, folder and reason. Expand **Document category and suggested facts** for quoted evidence. Uncertain/shortened inputs are flagged. Evidence supports review; it does not prove the interpretation is correct.
5. Correct the name/folder if needed. A new subfolder is shown explicitly. Add an optional filing preference or correction.
6. Choose **Approve & file**. The service validates the current source bytes, actor, settings/guidance/review revisions, destination and collisions under the filesystem writer. An altered server destination requires a second exact confirmation. Successful filing opens the next inbox document. **Decline suggestion** leaves the original in intake.

Leave **Remember this approved filing as an example** checked to teach future suggestions using the approved name, folder, category, summary and preference. This is retrieved context, not model fine-tuning or an automatically enabled rule. New approvals do not invalidate every other queued proposal. **Settings › AI reader › Remembered filing examples** shows the latest 30 and lets you **Forget example**; forgetting invalidates outstanding context and never moves the filed document.

Approving a filename and destination does not attest that every AI fact is human-verified. The manual **Use rules or enter facts manually** section remains available. Its **Read facts only with AI** uses configured types, their recognition guidance, enabled-rule hints and fact names (up to 31 types plus `unknown`, 64 fact names and 16,000 serialized context characters). **Use suggestion**, fact editing and **Confirm & file** retain the original deterministic-rule workflow. Existing fact-only proposals are preserved when upgrading.

**Read new inbox documents automatically** queues ready, indexed, unreviewed intake files in rotating batches. It only creates proposals. It does not overwrite manual reviews, approve facts or enable unattended filing. Cancelled/failed jobs are not repeatedly requeued with identical source, review and guidance; use an explicit retry. Changed inputs can produce a new job.

## Durability and boundaries

Jobs are stored in PostgreSQL with their source fingerprint/path, model, context snapshot, settings/review revisions and attempt count. The app runs one document-reading job at a time and releases the filesystem writer during inference. Startup recovers interrupted work without resetting the three-attempt limit. Unavailable storage waits until it can be revalidated. Document reading, guidance drafts, document questions and folder planning now share one inference slot within the app. Other applications using the Ollama server remain outside this scheduler.

Changing local-model settings cancels pending/running work. Cancellation also signals the active HTTP request; a late result cannot revive the job. Source changes, moves, manual-review edits or guidance changes discard stale responses. Loading a successful proposal into the editor checks its bindings and source bytes again. Reviewed facts and the filing journal remain separate from identification jobs.

For identification, only indexed text, the filename and bounded context are sent to the configured Ollama server. Guidance drafting instead sends canonical instructions and recent saved conversation messages. No document binary or absolute server path is included. Model output is parsed against a strict schema and displayed as escaped text. OCR is explicitly untrusted evidence and cannot authorize operations. The adapter never executes model tools, changes rules or moves files. Destination IDs and portable names are validated into an exact plan; model text is never used as an arbitrary filesystem path.

The additive `AddLocalIdentification` migration creates settings, document-type and durable-job tables. `AddAIOrganizing` adds the organizing mode, exact filing plans and remembered examples without changing existing jobs into organizing jobs. Existing documents and filing rules are preserved. Production use still needs a real installed Ollama model and extraction-quality checks on your hardware. Automated verification uses synthetic documents with a protocol-compatible test fixture, not a real language model or a claim about model accuracy.

## Verification

`tests/DocumentStorage.Checks` includes Ollama protocol/schema checks and real PostgreSQL/filesystem tests for queueing, retries, cancellation, restart recovery, guidance/review/content changes and human-review boundaries. `node tests/browser/organizing.mjs` covers novel categories, existing imperfectly spelled destinations, corrections, one-click filing, a new subfolder, decline, remembered examples/forgetting and mobile layout. `node tests/browser/ollama.mjs` retains the fact-only workflow and starts a loopback Ollama protocol fixture and drives the real login, settings, guidance, background worker and intake UI. It uses only synthetic files and disables identification before shutting down the fixture. Run against a disposable local catalog, never real documents.

PDF text selection in the browser does not prove server extraction succeeded. The slip reports **Text ready for AI reading** only for indexed server text; otherwise it shows the extraction status and disables reading. An image-only PDF still needs an OCR layer. This milestone adds neither OCR nor vision inference.

[Document questions and folder planning](conversations.md) use the same reader connection and preserve private conversational history. The passage index backfills existing extracted text automatically.
