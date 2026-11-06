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

The Inbox reads top to bottom. The waiting documents are listed on the left. On the right, the selected document's preview sits on top, showing the top of the page, where a document says what it is (drag the viewer's corner for more, or use Expand). Below it is the filing slip. On a wide screen the slip splits in two: **what it is** on the left (kind, sender, title, date, tags and any fields) and **where it goes** on the right (the folder path, the new name and the arranging tools). Fields sit on their own underlines; the slip has no ruled background. The **Approve & file** bar stays at the bottom of the screen while the slip is in view, with Skip and "Remember as an example" beside it. A phone shows the same order in one column.

1. **Arrive.** A scanner writes into an inbox folder, or you drop files on the page. A file shows **Arriving** until it has stayed unchanged for a few seconds.
2. **Read it.** The filing slip reports server text readiness. With the reader enabled, **Read with AI** queues one document at a time and returns a summary, filename, folder and reason. An unfamiliar category does not require setup.
3. **Check the suggestion.** Edit the filename or folder, inspect evidence under **Document category and suggested facts**, and optionally add a filing preference. A proposed new subfolder is explicit. **Remember this approved filing as an example** is checked by default; remembered choices can be forgotten in Settings › AI reader.
4. **Approve & file.** Approval binds the exact move to this sign-in and source fingerprint. Changed inputs, settings or guidance block stale plans; a destination resolved differently by the server needs another confirmation. No file is overwritten. The next inbox document opens automatically. Declining leaves the file in intake.

**Use rules or enter facts manually** retains the original slip: known types/facts, **Read facts only with AI**, **Use suggestion**, **Save draft**, **Confirm facts**, **Make a rule from this** and deterministic **Confirm & file**. Approval of an AI filename/folder does not falsely mark every model fact human-verified. **Move by hand…** remains in the slip's menu.

Status stamps include **New**, **Queued**, **Reading**, **Suggested**, **Needs a choice**, **Text unavailable**, **Draft**, **Arriving**, **Changed**, **No rule** and **Ready**. The scan refresh does not itself mean a document has text ready for AI.

Inbox remembers the selected document and page for each sign-in, including across browser reloads. Explicit document links take priority, even beyond the first page. Filing, moving or deleting advances near the current place. Saved review drafts and AI proposals remain in the catalog; unsaved edits still require **Save draft**.

The slip's **More actions › Delete document…** confirms a move to recoverable Trash; restore through **History › Trash**. **Rescan text** beside the extraction status forces this document's text extraction even when its file has not changed. It rereads an existing PDF text layer, not OCR from pixels, and invalidates old AI suggestions. Afterward choose **Try again** or **Read again with AI** for a fresh attempt. **Reading details** shows the model, mode, state, exact submitted text excerpt (for new attempts) and saved folder/guidance context.

## Rules and types

A **type** is a kind of document: a name, how the reader recognizes it, and the facts it carries. A **rule** takes documents of one type (optionally only when facts match, such as `institution = Cedar Bank`), names a destination folder, and gives a filename pattern built from facts: `{statement_end:yyyy-MM-dd} - {institution} - {account_suffix}.{extension}`. The editor's fact chips build the pattern, and an example filename updates as you type.

Each rule card shows its switch (on or paused), what it takes, where it files and how it names. **Try on inbox** shows which inbox documents it would take, without moving anything. Every save is a new version, listed under **Versions**. A folder's detail panel in the Library lists the rules that file into it.

## Notes for the reader

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
