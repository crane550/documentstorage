# Ask documents and plan folders

Both workspaces use the configured local Ollama model in **Settings › AI reader**. They save conversations and background requests, so leaving a page does not cancel a reply. **Ask documents** and **Plan folders** are real routes in the menu. Neither feature needs a configured document type or naming rule.

## Ask documents

Ask a natural-language question, then follow up in the same conversation. Start across all indexed documents, choose a folder and its descendants, or use **Ask about this document** in the Library. Examples:

- “What interest rate is listed in my home-equity agreement?”
- “Compare the closing balances in these statements.”
- “What does the ministry post guide say about the west entrance?”

Answers cite source IDs such as `[S1]`. The source cards show the filename, relative location, exact quoted evidence and a PDF page when known. Click a source to preview the original beside the conversation; PDF citations open on that page. An Open in Library link keeps normal document tools available. Source links recheck the stable document identity, full indexed-text revision and quoted span. They follow app-managed renames/moves when the indexed evidence is unchanged; changed text requires a fresh question instead of silently substituting a new version. Saved excerpts remain part of the historical answer.

A PostgreSQL full-text passage index retrieves evidence before answering. Existing extracted text backfills automatically in batches without another OCR pass. The page reports how many documents are searchable and still being prepared. A first question waits for initial passage indexing. Missing OCR or a missing PDF tool still needs to be fixed through the existing extraction workflow; document chat does not add vision or OCR.

The first version uses English lexical/stemmed search, not embeddings or an autonomous multi-step research agent. A query ranks up to 100 candidate passages and supplies at most 16 excerpts/18,000 text characters, with at most four passages per document (12 for a single-document scope). A document without keyword matches can supply beginning/end passages for a summary. These bounds mean an answer is not an exhaustive audit of every matching document; narrow the scope or question for large comparisons. Indexed PDF text is still subject to the existing first-100-pages/500,000-character extraction limits.

Only retrieved evidence supports answers. Model-provided source IDs must refer to supplied passages, and quoted evidence must match them. A factual reply needs citations; unsupported/malformed citations fail the reply rather than becoming invented links. Quote matching does not prove the model's interpretation is correct. If there is no retrieved evidence, the app says so without asking the model to invent an answer. Cached indexed text remains usable while storage is offline, with an offline label; opening the original still requires connected storage.

## Plan folders

Choose an existing parent folder outside inboxes. Describe the structure and what belongs in each part, then refine it through follow-up messages. For example:

> Under the existing church safety ministry, make Training with Annual refreshers inside it, and an Incident reports folder. Keep all campuses together. Give each folder filing instructions.

The model sees existing folder IDs, paths, notes and general guidance. It returns a complete revised proposal in parent-before-child order, preferring existing folders even when their spelling is imperfect. Existing folders retain their IDs, names and parents. A plan can reuse them and propose replacement filing notes; it cannot rename, move or delete folders/documents, enable rules or grant permissions.

Edit names, parent choices and instructions directly in the proposal. Earlier planned nodes are available as parents for nested folders. Add/remove nodes yourself too. Blank instructions on a reused folder preserve its notes; current notes are available for comparison. **Review exact folder changes** resolves the actual server paths and full instructions, then **Create folders & save instructions** applies that reviewed plan. Notes are saved as canonical folder guidance with immutable history and a link to the original conversational request, ready for the organizing reader to use.

A plan supports up to 24 nodes and eight new levels beneath one scope; proposed instructions are limited to 2,000 characters per node. The model inventory is bounded to 150 folders/13,000 serialized characters of folder metadata and 500 characters of notes per folder. If folders are omitted, new model-created paths are blocked; start within a narrower parent. Full notes and folder structure, including omitted text, participate in stale-plan checks. All operations remain in the same storage location.

Executable previews expire after 15 minutes and belong to the requesting sign-in. Changed tree/guidance/settings, collisions, unavailable storage and unsafe paths block stale work. No existing file or directory is replaced. Folder creation uses durable per-step receipts and an ownership marker staged on the same root. A verified interrupted creation can resume with its original folder ID. Scans do not adopt a directory while its creation step is unfinished. Completed folders/notes are retained; a paused plan offers **Resume remaining folders** or **Cancel remaining steps**. Cancellation never removes an existing directory or its contents.

## Conversations and reader scheduling

The latest 50 unarchived conversations are listed separately in each workspace. Each conversation saves up to 100 requests/replies, has a fixed scope and belongs to the signed-in account. Start a new one to change scope; archive finished conversations without affecting any documents. Cancel a queued/running reply or retry a failed one. Changing reader settings also cancels queued/running conversations and signals the active local-model request. Restart interrupts an active reply visibly; it does not discard the saved request or automatically repeat folder application.

The latest three answered exchanges prime follow-ups, with bounded text and a compact latest folder-plan outline. They provide conversational context, not new document evidence. Earlier assistant answers never substitute for retrieved citations. Older messages remain in the visible history even when they are outside the model's context window. Long earlier folder instructions may be shortened in the outline; review the complete new proposal before accepting it.

Document identification, question answering, folder drafting and guidance drafting share one inference slot inside the application. No two app requests compete for Ollama at once. Interactive queued conversations are considered before the next document-reading job; passage indexing runs separately in small database batches. This does not control other applications using the same Ollama server.

Cloud-backed model metadata is refused before sending source text or folder context. Requests contain no absolute storage-root paths or document binaries. Document text, filenames and prior assistant replies are data, never permission to perform operations. Model prose is displayed as escaped text. These UI workspaces do not add MCP tools or widen existing assistant permissions.

## Verification

The additive `AddDocumentConversationsAndFolderPlans` migration retains existing files, index text, accounts, rules and prior AI jobs. It adds passage/search and conversation/plan tables plus a backfill marker. Existing catalog upgrade checks seed the old schema before migration.

Real PostgreSQL/filesystem checks cover retrieval, source/page offsets, Unicode boundaries, citation validation, actor ownership, idempotent messages, follow-ups, local-model checks, shared inference, cancellation, scoped plans, preserved notes/history, collisions and save-fault/resume behavior. `tests/browser/desk.mjs` drives real login, local reader setup, indexed answers, cited PDF page navigation, saved conversations, plan refinement, editable review, physical nested folders, canonical notes and mobile layout using synthetic documents and a protocol fixture. Fixtures verify integration, not real-model answer/planning quality.
