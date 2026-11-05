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

## Not yet

Refiling when a kind's pattern changes or two senders merge (the fluid tree), back-filling details
for documents already in the Library, MCP tools for kinds, senders, tags and details, and other
file formats beyond PDF, Word and plain text.

## Checks

`tests/DocumentStorage.Checks/SortingChecks.cs` (patterns, hints, Word text, the narrow prompt,
mapping, plans, filing, corrections, new kinds, undo, automatic filing and its switches, Library
details, merge). `node tests/browser/sorting.mjs` drives the whole loop against a loopback Ollama
stand-in on a disposable catalog, including Enter to file, tags, Filed/Undo, Kinds, Library search
and phone width. Neither proves model accuracy: try it on real documents on the server.
