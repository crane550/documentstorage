# The interface

The app is built around one loop: **documents arrive in an inbox, the reader says what each is (kind, sender, date, title, tags), Stash works out the folder and name from the kind's patterns, and you approve with Enter.** See [Sorting](sorting.md); the section on filing below describes the older slip and is kept for the rule workflow's history.

## Where things are

| Place | What it is for |
| --- | --- |
| **Inbox** (`/`) | Everything waiting for you: new documents, suggestions from connected assistants, and the first-run setup. |
| **Library** (`/library`) | Your folders and files as they are on disk: browse, search, upload, rename, move, trash, and each folder's notes. |
| **Kinds & tags** (`/kinds`) | Kinds (with folder and name patterns, record and file-without-asking switch), senders and tags. The older rules page is linked from it. |
| **History** (`/history`) | Every move, filing, trash and restore; **Trash** and **Recovery** are its other tabs. |
| **Settings** (`/settings`) | Storage locations, the AI reader (Ollama), and assistant (MCP) connections. Account links to the password page. |

The sidebar also holds the folder tree for every connected storage location. **Recovery** joins the sidebar, with a count, only while an operation is unfinished, and a banner names the paused storage on every page. **Search** and **Add to inbox** are always in the top bar, and you can drop files anywhere in the window.

## Filing a document

The Inbox is a desk of three columns that fit the window: the waiting list, the document, and its filing slip. Drag the bar between the document and the slip to give either more room (double-click it to even them out, or use the arrow keys). The ‹ in the list's header folds the list to a strip. Both are remembered on each device. The slip scrolls on its own with **Approve & file** pinned at its foot. A wide slip puts **what it is** beside **where it goes**; a narrow one stacks them. Fields sit on their own underlines.

**Where it goes** has two plain choices. **Folder:** *Built from details* (the kind's arrangement, edited with **Arrange folders & name**), or *Choose a folder*. Choosing opens a searchable folder-tree popup at the suggested folder or its nearest existing parent. Browse or search, create nested folders in place, and use the selected folder without leaving the slip. An optional typed path remains under **Type a folder path**. Below the folder, **Earlier ones went to** offers up to three folders where documents of the same kind or sender now are, one click each; when the built folder is already one of those, the slip just says how many went there before. **Name:** *Built*, *Original* or *Type it*. A field's Name or Folder button switches that part back to built. Typing in any detail updates the folder and name after a short pause, without leaving the box. Above the fields, **Find N empty with AI** looks only for the empty ones (code tries each field's printed words first), and **Set up fields with AI** suggests the fields the kind should carry, with what to look for, the words printed beside each and this document's values, to tick and keep. The note button on a kept field describes it by hand; once a field has printed words, the next document's value is filled in from them without the reader. See [Sorting](sorting.md#how-each-field-is-found). List rows show the end of each path, with the full path on hover. A phone shows the same parts in one column.

1. **Arrive.** A scanner writes into an inbox folder, or you drop files on the page. A file shows **Arriving** until it has stayed unchanged for a few seconds.
2. **Read it.** The filing slip reports server text readiness. With the reader enabled, **Read with AI** queues one document at a time and returns a summary, filename, folder and reason. An unfamiliar category does not require setup.
3. **Check the suggestion.** Edit the filename or folder, inspect evidence under **Document category and suggested facts**, and optionally add a filing preference. A proposed new subfolder is explicit. **Remember this approved filing as an example** is checked by default; remembered choices can be forgotten in Settings › AI reader.
4. **Approve & file.** Approval binds the exact move to this sign-in and source fingerprint. Changed inputs, settings or guidance block stale plans; a destination resolved differently by the server needs another confirmation. No file is overwritten. The next inbox document opens automatically. Declining leaves the file in intake. A document already in Stash (the same file, or the same text in another file) is stamped **Duplicate**; its slip links to the other copy and offers **Delete this copy…** or **Keep both**, and Enter won't file it until you choose. A filed document with the same kind, sender and date, or the same name where it would go, is flagged as a possible copy. Filed something wrong? **Inbox › Filed** has Undo for recent filings, and **Back to inbox** on any document in the Library sends it back with its details filled in (or, for a document Stash didn't file there, just moves it to the inbox).

**Use rules or enter facts manually** retains the original slip: known types/facts, **Read facts only with AI**, **Use suggestion**, **Save draft**, **Confirm facts**, **Make a rule from this** and deterministic **Confirm & file**. Approval of an AI filename/folder does not falsely mark every model fact human-verified. **Move by hand…** remains in the slip's menu.

Status stamps include **New**, **Queued**, **Reading**, **Suggested**, **Needs a choice**, **Text unavailable**, **Draft**, **Arriving**, **Changed**, **No rule** and **Ready**. The scan refresh does not itself mean a document has text ready for AI.

Inbox remembers the selected document and page for each sign-in, including across browser reloads. Explicit document links take priority, even beyond the first page. Filing, moving or deleting advances near the current place. Saved review drafts and AI proposals remain in the catalog; unsaved edits still require **Save draft**.

The slip's **More actions › Delete document…** confirms a move to recoverable Trash; restore through **History › Trash**. **Rescan text** beside the extraction status forces this document's text extraction even when its file has not changed. It queues extraction in the background, retains usable PDF text and runs local pixel OCR for images and scanned/text-poor PDF pages, and invalidates old AI suggestions. Text readiness updates when extraction finishes. Settings → **File formats** shows registered capabilities, recent extraction jobs and explicit retries. Afterward choose **Try again** or **Read again with AI** for a fresh attempt. **Reading details** shows the model, mode, state, exact submitted text excerpt (for new attempts) and saved folder/guidance context.

## Rules and types

A **type** is a kind of document: a name, how the reader recognizes it, and the facts it carries. A **rule** takes documents of one type (optionally only when facts match, such as `institution = Cedar Bank`), names a destination folder, and gives a filename pattern built from facts: `{statement_end:yyyy-MM-dd} - {institution} - {account_suffix}.{extension}`. The editor's fact chips build the pattern, and an example filename updates as you type.

Each rule card shows its switch (on or paused), what it takes, where it files and how it names. **Try on inbox** shows which inbox documents it would take, without moving anything. Every save is a new version, listed under **Versions**. A folder's detail panel in the Library lists the rules that file into it.

## Notes for the reader

**Settings › AI reader** lists named server/model pairs in priority order, with connection/model checks, Up/Down, disable and remove controls for fallbacks. Last runtime checks and Recent reading show availability and the actual reader/model used. Idle unloading lives under Limits. Offline computers are skipped; document work waits durably when no reader is available. See [desktop setup](ollama-windows.md) and [routing behavior](local-ai.md).

Guidance is written on sticky notes where it applies: **Folder notes** on each folder in the Library, and **General guidance** in Settings › AI reader. Type descriptions live with the type in Rules. With the reader on, a note can also be changed by asking (“Tell the reader what to change…”). The reader drafts a new version, and you choose **Use this version** or **Discard**. Every saved version and the conversation behind it stay under the note.

## Assistants

Settings › Assistants creates one OAuth connection per MCP client and shows the client secret once. Suggestions from assistants (guidance, paused rules, one exact filing) appear at the top of the Inbox and in Settings. They change nothing until you accept them in the browser.

## Document conversations and folder plans

**Ask documents** (`/ask`) holds saved conversations, a fixed document/folder/library scope, cited answers and a side-by-side source preview. PDF evidence opens at its page. **Ask about this document** in the Library starts a focused conversation. **Plan folders** (`/folder-planner`) accepts a parent scope and a conversational request, shows editable nested nodes and notes, then an exact review before creation. Folder receipts link directly to the real folders. Both pages retain work across navigation and support mobile layouts. See [Conversations](conversations.md).

## Updates and version

The bottom of the menu shows the running version, build number and short commit. **Update** opens Settings › Updates, with available changes, a separate install confirmation and progress through backup/restart. A one-time Windows task installation enables the controls; future releases install from the browser. See [Updates](updating.md).

## Design language

The app is meant to feel like a well-kept desk. Cardboard is used for the frame: the sidebar, dropdown menus, dialog headers, the drop box and the sign-in page. Paper is used for the work. Other details:

- Index cards for the filing slip and rule cards.
- A manila folder for "where it goes".
- Rubber-stamp labels for status.
- Sticky notes for guidance.
- Typewriter (monospace) type for filenames and paths.

The textures are generated, not photographed. `tools/textures/generate.py` writes the seamless `wwwroot/images/kraft.webp` and `paper.webp`.

Colors stay inside the original palette, now set as tokens at the top of `wwwroot/app.css`. The app uses system fonts only, so no page asks another origin for anything.

## For developers

- **`WorkspaceState`** (scoped, one per circuit) holds the library overview, the counts and the notice strip. Pages change files only through `LibraryService`, then call `Workspace.RefreshAsync()`. The layout refreshes it every 30 seconds while the background scanner works. Use `Workspace.Notify` for confirmations; errors stay until dismissed.
- A component that only reads the workspace and takes no parameters should `@inherits WorkspaceComponent`. Blazor does not re-render a parameterless child when its parent does, so these components repaint themselves when the overview changes.
- Pages are real routes, and their state lives in the address (`/library?folder=…&doc=…`, `/rules?type=…`, `/rules?from={document}`, `/?open={document}`, `/settings?tab=…`), so Back, refresh and links behave.
- Shared pieces: `Modal`, `Menu` (a real button with `aria-expanded`, not `<details>`), `Stamp`, `EntryDialog` (new folder, rename/move and trash, each recheck-bound by path), `ConnectStorageDialog`, `GuidanceCard`, `FilingSlip`, `RuleEditor`, `TypeEditor`, `ProposalDialog`.
- Field styles in `app.css` use `:where()` so they carry no specificity and component rules override them without `!important`.
- Razor reads `text@expression` as an e-mail address. Write `v@(rule.Revision)` and wrap text that ends right before an `@if` in its own element.
- `tests/browser/ui.mjs` holds the shared steps (sign in, connect storage, open a folder, make an inbox, rescan, open an inbox item). A layout change should usually mean editing that file, not every check.

**Saved document description** appears in Inbox and in a selected Library document. It holds an editable title, summary, reusable tags and organizations plus saved suggested facts. Save these independently of naming/filing; search includes them. In Inbox, **Suggest folder from saved description** reuses that information with current folders and sends no original OCR. Unsaved edits must be saved before requesting placement. Changed documents show a historical-description warning and require a fresh reading. **Reading details** distinguishes placement from full text reading.

**Assistant changes** in Inbox and Settings › Assistants separates pending changes from applied changes waiting for verification. It shows who changed what, the explanation/request, and before/after values. Editing connections have a **Change handling** selector: review before applying or apply then verify. **Mark verified** records a check after application. Explicitly authorized go-ahead changes remain in this history.

**Folder selection** uses the same searchable tree in Inbox, rename/move, new-folder parent and rule editors. It preserves unsaved editor values; created folders are saved immediately, and choosing a destination does not approve a file move. See [Folder management](folders.md).
