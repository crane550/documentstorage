# Sorting: kinds, senders, tags and the approval list

The inbox loop, as built on 5 Nov 2026 (see [direction.md](direction.md) for why).

> A document lands in an inbox. The reader says what kind it is, who it's from, its date, a short
> title, tags and the kind's fields. Stash works out the folder and name from the kind's patterns.
> You correct details with a click and press Enter. A kind you trust can file itself.

## What Stash knows

- **Kinds** (`DocumentKind`): a name, a one-line description the reader sees, the fields to pull,
  a **folder pattern** and a **name pattern**, plus an *automatic* switch and a record (approved,
  corrected, right in a row). A starter list is seeded on first use (bank statement, invoice,
  purchase order, bill, receipt, medical, vehicle, tax document, insurance, legal, letter, school,
  and the catch-all **Everything else**). The reader may propose a new kind; approving the first
  document makes it, with the default patterns `{kind}/{from}` and `{date} {from} {title}`.
- **Senders** (`Correspondent`): one name and other spellings. A correction teaches: when the reader
  said "CEDAR BANK N.A." and you chose "Cedar Bank", that spelling is kept for next time. Merge
  keeps the target's name and the other's name as a spelling.
- **Tags** (`Tag`): lowercase, hyphenated, with spellings, merge, rename and delete.
- **Details** (`DocumentDetail` + `EntryTag`) live on the document itself, not the intake review:
  kind, sender, date, title, tags, fields. They stay through filing and app moves, show in the
  Library's document panel, and are searchable: plain words match titles, kinds, senders, spellings
  and tags; `kind:Invoice`, `from:Cedar_Bank` and `tag:house` narrow exactly (`_` for a space).

Manage them at **Kinds & tags** (`/kinds`).

## Fields, configured by using

Each kind carries fields (`DocumentKind.FieldsJson`, keys like `statement_end_date`, shown as
"Statement end date"). On the slip:

- **+ Add a field** takes plain words and makes the key (`FieldKey`); the field is saved on that
  document only.
- **Keep for {kind}** (`AddKindFieldAsync`) adds it to the kind, so the reader is asked for it on
  every document of that kind from then on.
- **Name** and **Folder** on every field (new kinds too) add or take out the field's token in this
  document's arrangement: at the end of the name, or as a new last folder level (a date field
  files by year there: `{statement_end_date:yyyy}`).
- **Edit kind…** opens the kind's whole form without leaving the Inbox.

A brand-new kind has no Keep buttons: its fields travel with the document and become the kind's
fields when it is approved.

## How each field is found

Each kept field has a **guide** (`FieldGuide`, in `DocumentKind.FieldGuideJson`, keyed by field):
a **type** (text, date, money, number, ID number), **what to look for** (one line the reader is
told) and the **words printed beside it** ("Beginning balance", "Previous balance"). Until someone
says, the type comes from the name (`DefaultFieldType`: *date* → date, *balance*/*total*/*amount* →
money, *number*/*ending* → ID number). Values are kept in one form per type (`NormalizeFieldValue`):
dates as `yyyy-MM-dd` (so patterns can format them: `{statement_end_date:yyyy-MM}`), money as
`1234.56`, a masked number by the digits it shows (`****5678` → `5678`).

- **Matching** (`ValueFound`): a value counts when it's in the text however it's written: `$1,204.33`
  for 1204.33, May 31, 2025 for 2025-05-31, `****5678` for XXXX5678, any capitals or spacing. A value
  the reader gave that still isn't in the text is left empty, and the slip says so ("Left Account
  type empty: the reader said …").
- **Printed words** (`LabelFind`): when a card is built, an empty field whose guide has printed words
  is filled from the rest of that line (or the next), read as the field's type, with source *text*.
  No model is asked. This never makes a card sure: a sure card already has its pattern fields.
- **The reading prompt** gives the five likeliest kinds' guides as `field_notes`; it shows 6,000
  characters of text and takes up to 12 fields.
- **Describe** (the note button on a kept field) edits the guide in place (`SetFieldGuideAsync`).
  The kind form lists every field's guide under **How each field is found**.

## Asking the reader from the slip

Both share the model slot with the queue (`modelWork`), run one at a time, and are checked against
the same text and reader settings afterwards (`AskReaderAsync`); a change refuses the answer.
**Stop** cancels.

- **Find N empty with AI** (`FindFieldsAsync`) tries each empty field's printed words first, then
  asks the reader only for the rest (`FindFieldsAsync` on the client: the kind, sender and date as
  context, each field with its type and guide, and up to 12,000 characters: the start, then the
  passages around the fields' printed words, `FieldExcerpt`). Values are checked as above and fill
  only fields still empty; nothing is saved until you approve.
- **Set up fields with AI** (`DraftKindAsync`) asks which fields this kind should carry, with each
  one's type, what to look for, printed words and this document's value. The review dialog
  (`KindSetupDialog`) lists the kind's own fields first (a guide you wrote wins over the
  suggestion), ticks new ones that this document shows, and offers the suggested description.
  **Keep N fields** (`SaveKindDraftAsync`) adds the ticked fields and their guides, never removes
  one, and fills this document's empty fields from the checked values. A kind not made yet is made
  then, with the slip's arrangement.

## Arranging the name and folders

**Arrange name & folders** in Goes To opens the arranger (`PatternComposer`) in place: the folder
levels and the name as chips (Kind, From, Title, Date, each field, and plain words). Drag a chip
to move it within a level, between levels, onto **+ folder** for a new level, or into the name; or
click a chip and then click where it goes (touch screens). ⇄ on a date chip changes how it's
written (full date, year-month, year, month name); × removes; arrow keys move a focused chip and
Delete removes it. The palette adds parts; **Text** adds plain words. `PatternParts` (Core, pure)
splits patterns into chips and joins them back.

The arrangement belongs to this document (`SortChoice.FolderPattern` / `NamePattern`) until it's
approved. **Use for every {kind}** (ticked by default once you arrange) saves it to the kind in
the filing's own transaction (`KeepPatterns`); the document then follows its kind, and filed
documents that followed the old arrangement show under Tidy up. Unticked, only this document is
arranged and it stays where it was put. A brand-new kind is created with the arrangement its
first document was filed with. Rearranging the name stops keeping the original name. "Type a
folder or name…" still overrides both for one document. The kind form (Kinds page and Edit kind…)
uses the same arranger, with the typed patterns under "Type the patterns".

## Patterns

Tokens: `{kind}`, `{from}`, `{title}`, `{date}` (also `{date:yyyy}`, `{date:yyyy-MM}`,
`{date:MM}`, `{date:MMMM}`) and any field (`{invoice_number}`; a field holding an ISO date takes
the same formats). `SortPatterns` (Core, pure) renders them:

- A folder level whose tokens are all empty is left out (`Banking/{from}/{date:yyyy}` with no
  sender is `Banking/2026`), so a missing detail never makes an empty or odd folder.
- Every value is cleaned into one portable segment: separators and reserved characters become
  dashes, dangling separators and empty brackets go. Model text can never become a path.
- Patterns are validated when saved: closed braces, known token shape, no `..`, no folders in a
  name pattern.

## Reading (the model)

`SortingContextAsync` builds one narrow request for a small local model (qwen3:4b-instruct on a
CPU is the target): the filename, the likely kinds with descriptions and fields (all of them up to
20, ranked by plain matching), up to 25 senders and 30 tags (matched ones first, then most used),
three approved examples, general guidance and the first 4,000 characters. `OllamaClient.SortAsync`
sends it with `think: false`, `num_ctx` 8192 and a small output budget, and asks for kind, sender,
date, title, tags, fields with quotes and a confidence. No folder ids, no filenames.

`DocumentHints` (Core, pure) reads the filename and first page without the model: whether the name
is descriptive (an emailed `Acme_Invoice_INV-20391.pdf`) or a scanner's (`20261105_143022.pdf`,
`Scan 12.pdf`), dates in ISO, compact, US and written forms, and known senders, tags and kinds named
in the filename or text. With the reader off, the card starts from these.

A reading binds only to the document's bytes (`SortContextHash` is constant), so renaming folders,
adding kinds or approving other documents never throws away a slow reading; approval works the
path out fresh anyway. Automatic reading queues these sorting jobs.

## Checks on the answer

`FromReply` maps the reply to what Stash knows and marks each detail's source (reader, filename,
text, person, guess): kind and sender by name or spelling (plural and longer-name forms included),
tags likewise with at most one new tag, fields only the kind's and only with a quote found in the
text, and the date only trusted when it is written in the document; otherwise it's a guess.
A card is **sure** when the kind and sender are known, the date is written, the reader said high,
no new tag is needed, the kind's pattern fields are all present and the plan has no problem.

## Approving

The Inbox (`/`) lists cards with what each is and where it will go. The slip (`SortSlip`) shows
the details as fields with source dots (amber ring = guess), a live destination with new folders
marked, the name (kept by default when it's descriptive), "Change folder or name…" for a one-off
override, and Approve & file. **Enter** files it and opens the next; **J/K** move between cards
(`wwwroot/sort-keys.js` only clicks buttons Blazor rendered). Tick several, or **Approve N sure**,
to file as suggested.

The **Name → Built / Original** selection is remembered per kind when the person explicitly chooses one and approves the filing. Later documents of that kind use it, including saved reader replies and recurring-document memory, across navigation and restarts. Until a choice is learned, descriptive filenames keep their original names as before. **Type it** is a one-document name and does not change this default. Merely previewing or skipping a choice does not teach it, and changing the default does not rename earlier files or request another AI reading. The preference commits with the filing, including recovery. The slip says when approval will keep the selected default.

Under the folder, **Earlier ones went to** offers up to three folders from history
(`FolderSuggestionsAsync`): where earlier documents of the same kind and sender are now (so a hand
move counts), then the same kind, then the same sender, busiest first. Inbox folders and the root
never appear. When the planned folder is already the top one, the slip says so instead ("Same
folder as 3 earlier ones"). Clicking a chip chooses that folder for this document only.

`FileSortedAsync` checks the bytes against the hash the card showed, plans again under the writer,
records an `OrganizationFiling` carrying the details (`DetailsJson`) and moves through the
existing journal (`Kind = "Organize"`). Missing folders along the path are created in the same
operation (`EnsureOrganizationFolderAsync` creates nested segments). A taken name gets " (2)".
On completion, in the same transaction (also when Recovery completes it): the details, new kind,
sender and tags, the kind's record, a learned spelling and the remembered example.

## Copies already in Stash

Every Inbox card looks for other copies of its document (`DuplicatesAsync`, `SortCard.Copies`):

- **The same file**: the same fingerprint. Files of the same size that never came through an inbox
  (so have no fingerprint yet) are fingerprinted on the spot, and keep it, so the whole Library
  counts, not just what Stash filed.
- **The same text in a different file**: `CatalogEntry.TextHash` (SHA-256 of the extracted text, set
  by the scanner; the `AddDuplicateDetection` migration fills it for documents already read). For
  text of at least 80 characters, such as a PDF downloaded twice.
- **The same details**: a filed document with the same kind, sender and date, and the same values
  for every field the kind's folder or name uses (two accounts' statements on one day stay apart).
- **The same name**: the built name is already taken in that folder (`SortPlan.TakenBy`; the new
  one would get " (2)").

The first two are certain. A certain copy is stamped **Duplicate**, is never sure (so it never files
itself and never goes with **Approve N sure** or a ticked batch), and Enter on its slip says why
instead of filing. The slip names each copy with a link to it (Library, Inbox, or Trash) and offers
**Delete this copy…** (to Trash, recoverable) or **Keep both**, which lets it file. The likely ones
show the same notice in amber, with **It's a different document**, and don't hold anything up.

## Trust and undo

- **Filed** lists recent filings with Undo. Undo moves the document back to the inbox it came from
  (the details stay, so the card returns filled in), forgets its example, resets the kind's run,
  and if the filing was automatic switches that kind back to asking first.
- **Back to inbox** on any document in the Library (`ReturnToInboxAsync`) is the same undo when
  the document is still where Stash filed it. Otherwise (moved by hand, filed before sorting, or
  its inbox is gone) it moves the file through the ordinary journaled move into this storage's
  shallowest inbox folder. Either way the Inbox opens on it.
- Each kind's record: approved, corrected (kind, sender, date, folder or name changed), and right
  in a row. After 10 in a row the Kinds page suggests letting it file itself.
- **File without asking** is per kind and yours to switch; a master switch on the Kinds page
  (`OllamaSettings.AutoFileEnabled`) stops all of it. After a reading, `TryAutoFileAsync` files a
  card only when its kind is switched on, the card is sure and nobody has touched its details;
  the actor is `automatic`. New kinds, new senders and guesses always wait.

## The fluid tree: Tidy up

Each document's details record whether its folder and its name **follow its kind's patterns**
(`DocumentDetail.FollowsFolder` / `FollowsName`). Filing from the Inbox sets them: the folder
follows unless you picked one by hand; the name follows unless it was kept or typed. Documents
filed before this existed are marked by the `AddRefiling` migration.

When a kind's pattern changes, a sender is renamed or merged, or details are corrected, documents
that follow their kind may now belong somewhere else. `RefilePlanAsync` works that out fresh (only
the parts that follow; inbox documents are left to the Inbox; two headed for one name get a
number) and **Tidy up** (Kinds & tags › Tidy up) lists each with its from and to paths and the
folders it would make. Nothing moves until you choose **Move**. `RefileAsync` checks the
destination is still the one shown (a different " (n)" is allowed), creates missing folders, and
moves through the journal. Saving a kind or sender says how many documents are affected; a merge
opens Tidy up for that sender. In the Library, **Keep it where its kind puts it** switches one
document to follow and offers **Move it**.

## Back-fill: the existing Library

Tidy up also lists Library documents without details. **Read N with AI** queues sorting readings
for up to 100 at a time (`QueueBackfillAsync`, optionally one folder); these jobs are marked
`Backfill`, run outside the inbox rules, and never file anything, even for a kind that files
itself. Each reading waits as an editable row: **Save here** saves the details where the file is,
**Save & move** also makes it follow its kind and re-files it, **Skip** leaves it out for good.
Accepting counts toward the kind's record and learns sender spellings, like an Inbox approval.
Documents read by the older organizing reader keep their saved description (title, tags,
organizations), which now pre-fills an Inbox card where plain matching found nothing.

## Assistants (MCP)

With catalog access: `list_kinds`, `list_senders`, `list_tags`, `find_documents` (by kind, sender,
tag; words need document-read permission), `get_document_details`, `list_tidy_up`. With the new
**sorting.propose** permission ("Suggest document details, kinds, merges and tidy-ups"):
`propose_document_details`, `propose_kind` (with optional `field_guides`: per field key, a type,
`look_for` and `labels`; left out keeps the kind's own; `list_kinds` returns them), `propose_merge`, `propose_refile`. Each is an ordinary
`AssistantProposal` (context hash, idempotency key, source request), checked with the same rules
the pages use when proposed, and shown in the Inbox under **From your assistants** with exactly
what it would do. Accepting runs the same services (`DecideSortingProposalAsync`: claimed as
Applying, applied, then Applied; a failure returns it to Pending). An assistant can't approve,
can't switch a kind to file by itself (a kind proposal keeps that switch as it was), and moves
files only through an accepted re-file of documents Tidy up already lists.

## Regulars: recurring documents from memory

A **regular** (`SortRoute`) is one kind from one sender, such as bank statements from your bank.
Every approval (Inbox, back-fill, or "Looks right" on something that filed itself) adds to its
record and keeps the last five as memory: title, tags, fields and **how the date was picked**
(`DateRules`: from the filename, the latest, first or earliest date in the first page).

Once a regular has three remembered approvals, a new document whose first-found sender is that
regular's, and where that kind clearly dominates for the sender, is filled in **from memory**
(`FromMemory`) with no model call: the kind, the date picked the same way, fields whose value never
changed (only if that value appears in the document), a steady title and the shared tags. It is
sure when the kind's pattern can be filled completely. A sure memory beats a reading; automatic
reading skips documents memory is sure of, so the model's time goes to what is new.

Trust can be narrower than a kind: Kinds & tags › **Regulars** lists each with its record and its
own **File without asking**. After five right in a row the Inbox offers "Let them file
themselves?" ("Not now" waits five more). The worker's `AutoFileFromMemoryAsync` files sure
memory cards of trusted regulars (or kinds) every 15 seconds, **with or without the reader**.
Filed shows them as filed by themselves with **Looks right** (counts toward the record) and
Undo (switches that regular, and its kind, back to asking; the document never files itself
again). Merging senders adds their regulars together. Catalogs filed before regulars existed
rebuild them once from approved filings at startup (`RebuildRegularsAsync`).

## Not yet

Other file formats beyond PDF, Word and plain text; one set of tags (older descriptions only
pre-fill cards); measuring the real model on the server.

## Checks

`tests/DocumentStorage.Checks/SortingChecks.cs` (patterns, hints, Word text, the narrow prompt,
mapping, plans, filing, corrections, new kinds, undo, automatic filing and its switches, Library
details, merge, re-filing, back-fill, assistant proposals), `KindFieldChecks.cs` (field keys, dates, keeping fields, chips round-tripping, one-off and kept arrangements, new kinds) and `RegularsChecks.cs` (date rules, memory, trust, nudges, confirm, undo, rebuild, merge). `node tests/browser/sorting.mjs` drives the whole loop against a loopback Ollama
stand-in on a disposable catalog, including Enter to file, tags, Filed/Undo, Kinds, Tidy up (a pattern change and its moves),
back-fill (read, Save & move), Library search and phone width. `DocumentStorage.McpChecks` covers
the sorting tools over the real protocol. `node tests/browser/regulars.mjs` drives three readings, a fill-in from memory, the nudge, a statement filing itself and Looks right. Neither proves model accuracy: try it on real documents on the server.
