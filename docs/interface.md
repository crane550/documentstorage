# The interface

The app is built around one loop: **documents arrive in an inbox, you say what each one is, and a rule says where that kind of document goes.** Once a rule exists, the next document like it already shows its destination and only needs your confirmation. Everything on screen serves that loop or gets out of its way.

## Where things are

| Place | What it is for |
| --- | --- |
| **Inbox** (`/`) | Everything waiting for you: new documents, suggestions from connected assistants, and the first-run setup. |
| **Library** (`/library`) | Your folders and files as they are on disk: browse, search, upload, rename, move, trash, and each folder's notes. |
| **Rules** (`/rules`) | Kinds of documents (types and the facts they carry) and the rules that file them. |
| **History** (`/history`) | Every move, filing, trash and restore; **Trash** and **Recovery** are its other tabs. |
| **Settings** (`/settings`) | Storage locations, the AI reader (Ollama), and assistant (MCP) connections. Account links to the password page. |

The sidebar also holds the folder tree for every connected storage location. **Recovery** joins the sidebar, with a count, only while an operation is unfinished, and a banner names the paused storage on every page. **Search** and **Add to inbox** are always in the top bar, and you can drop files anywhere in the window.

## Filing a document

1. **Arrive.** A scanner writes into an inbox folder, or you drop files on the page. A file shows **Arriving** until it has stayed unchanged for a few seconds.
2. **Say what it is.** The filing slip next to the preview asks for a **document type** (one tap on a known type lays out its facts) and the **facts** that name it: dates as `yyyy-mm-dd`, plus things like the institution or the last four digits. If the AI reader is on, **Read with AI** suggests both, with quoted evidence. Nothing is copied in until you choose **Use suggestion**.
3. **See where it goes.** As you type, the manila folder on the slip shows the destination and the exact new filename from the enabled rules. If no rule takes it, the slip says so and offers **Make a rule from this**, which opens the rule editor already filled in from this document. Saving it brings you back to the same document.
4. **Confirm & file.** This marks the facts as checked by you, asks the server for a fingerprint-bound preview, and files only if that preview is what the slip showed. If anything differs (another tab, a new rule), the slip shows the server's version for a second look instead of moving the file. The next document opens automatically.

**Save draft** keeps unconfirmed facts. **Confirm facts** marks them checked without filing, for example while you write the rule. **Move by hand…** (in the slip's ⋯ menu) moves a one-off document without a rule.

Status stamps in the inbox list: **New**, **Suggested** (the reader finished), **Draft**, **Reading**, **Arriving**, **Changed** (the file changed after its facts were saved), **No rule** (facts confirmed, nothing matches yet) and **Ready**.

## Rules and types

A **type** is a kind of document: a name, how the reader recognizes it, and the facts it carries. A **rule** takes documents of one type (optionally only when facts match, such as `institution = Cedar Bank`), names a destination folder, and gives a filename pattern built from facts: `{statement_end:yyyy-MM-dd} - {institution} - {account_suffix}.{extension}`. The editor's fact chips build the pattern, and an example filename updates as you type.

Each rule card shows its switch (on or paused), what it takes, where it files and how it names. **Try on inbox** shows which inbox documents it would take, without moving anything. Every save is a new version, listed under **Versions**. A folder's detail panel in the Library lists the rules that file into it.

## Notes for the reader

Guidance is written on sticky notes where it applies: **Folder notes** on each folder in the Library, and **General guidance** in Settings › AI reader. Type descriptions live with the type in Rules. With the reader on, a note can also be changed by asking (“Tell the reader what to change…”). The reader drafts a new version, and you choose **Use this version** or **Discard**. Every saved version and the conversation behind it stay under the note.

## Assistants

Settings › Assistants creates one OAuth connection per MCP client and shows the client secret once. Suggestions from assistants (guidance, paused rules, one exact filing) appear at the top of the Inbox and in Settings. They change nothing until you accept them in the browser.

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
