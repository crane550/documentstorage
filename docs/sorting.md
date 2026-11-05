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

`FileSortedAsync` checks the bytes against the hash the card showed, plans again under the writer,
records an `OrganizationFiling` carrying the details (`DetailsJson`) and moves through the
existing journal (`Kind = "Organize"`). Missing folders along the path are created in the same
operation (`EnsureOrganizationFolderAsync` creates nested segments). A taken name gets " (2)".
On completion, in the same transaction (also when Recovery completes it): the details, new kind,
sender and tags, the kind's record, a learned spelling and the remembered example.

## Trust and undo

- **Filed** lists recent filings with Undo. Undo moves the document back to the inbox it came from
  (the details stay, so the card returns filled in), forgets its example, resets the kind's run,
  and if the filing was automatic switches that kind back to asking first.
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
`propose_document_details`, `propose_kind`, `propose_merge`, `propose_refile`. Each is an ordinary
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
details, merge, re-filing, back-fill, assistant proposals) and `RegularsChecks.cs` (date rules, memory, trust, nudges, confirm, undo, rebuild, merge). `node tests/browser/sorting.mjs` drives the whole loop against a loopback Ollama
stand-in on a disposable catalog, including Enter to file, tags, Filed/Undo, Kinds, Tidy up (a pattern change and its moves),
back-fill (read, Save & move), Library search and phone width. `DocumentStorage.McpChecks` covers
the sorting tools over the real protocol. `node tests/browser/regulars.mjs` drives three readings, a fill-in from memory, the nudge, a statement filing itself and Looks right. Neither proves model accuracy: try it on real documents on the server.
