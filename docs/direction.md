# Direction: the inbox-to-folder loop (accepted 5 Nov 2026)

Accepted by Alex on 5 Nov 2026 ("Let's get going on it"). Steps 1–5 of the build order are built;
see [sorting.md](sorting.md). This replaces the earlier feature priorities.

## What Stash is for

> Documents land in the inbox: scans (already OCR'd, plain names) and files saved from email
> (often well named). Stash reads each one, works out what it is, who it's from, its date and tags,
> names it and files it. Until you trust it, every filing waits for a quick approval that is easy to
> correct. Once you trust it, a kind of document files itself and you just see what was filed.

Life's paperwork, everything: mostly bank statements, invoices and purchase orders, then bills,
medical results, receipts, policies, flyers, business cards, vehicle titles and registrations, legal
papers, homework, letters, recipes. PDF and Word first; other formats later.

## What to expect from this machine

The server is a Dell OptiPlex 7090 (11th-gen Core i7, 16 GB, no graphics card) running
`qwen3:4b-instruct` in Ollama on the CPU. That's a small model, and speed doesn't matter: it can
chug through the inbox in the background. What matters is that every question it gets is **narrow,
with short lists of answers**, because a 4B model is good at "which of these?" and poor at open
design.

Rough guess, not measured: on this CPU, Ollama reads the prompt at tens of tokens a second and
writes a few to ten. Today's request is about 7,000 tokens (12,000 characters of document plus a
16,000-character folder list), so a few minutes per document is likely spent just reading the
prompt. A narrow prompt of 1,500–2,000 tokens should be well under a minute. Step 3 below measures it.

| Job | A 4B model | Who should do it |
|---|---|---|
| "Which of these kinds is this?" (list of ~10 likely ones) | Good | Model |
| Date, who it's from, amount, account ending, invoice/PO number | Good, when checked against the text | Model, verified by code |
| Pick tags from a shortlist | Good | Model |
| A short human title ("Water bill", "PO 4471 – Acme") | Good | Model |
| "None of these fit; call it X" | Fair | Model proposes, a person confirms the first time |
| Choose among 120 folders by id | Poor | Code, from the kind |
| Compose a consistent filename | Inconsistent | Code, from a pattern |
| Redesign the folder tree, answer open questions over the library | Poor | An outside assistant (Claude, ChatGPT) over MCP |

So: **the local model classifies and extracts; code builds the folder and name; a big outside
model is the one that takes liberties with structure**, through proposals you approve in Stash.

## Where the current code stands

Kept: storage roots, scanning and settle checks, `pdftotext` extraction, the journaled move with
recovery, previews, the Inbox and filing slip, the durable one-at-a-time Ollama queue, the evidence
check (a detail whose quote isn't in the text is dropped), and the MCP server with OAuth.

Off track for this goal:

1. **One prompt does everything** (`OrganizationProtocol.cs`): invent a type, invent fact names,
   pick a folder GUID from up to 120 (all of them in the schema's enum), write a filename, explain.
   That's the hardest possible question for a 4B model, and the slowest.
2. **AI suggestions and rules don't meet.** Types and fact names are invented fresh each time, so a
   rule never fires on what the AI read, and there's no path to "file it without me".
3. **Nothing is kept after filing.** Type and facts stay on the intake review and the job. A filed
   document is a name, a path and its text; search is `ILIKE`. No tags, date or sender.
4. **The incoming filename is never parsed.** An emailed `Acme_Invoice_INV-20391_2026-10-14.pdf`
   already says kind, sender, number and date.
5. **Any folder or guidance change throws away every waiting suggestion** (the context hash covers
   every folder), so one rename means re-reading the inbox at minutes per document.
6. **Prompts are far bigger than needed**, at `num_ctx` 16384. (The instruct model doesn't think,
   so that isn't the slowness.)
7. **Word documents aren't read at all**: anything but PDF, txt, md and csv is "Filename only".
8. **Approval is built as a security gate, not a workflow**: 15-minute expiring, sign-in-bound,
   context-hashed tokens, one document at a time. Safe, but not quick to get through or correct.
9. **Scope has run ahead of the loop**: document Q&A, folder planning, guidance versions, a
   workbench and PDF tools next.

## The model of a document

- **Kind**: Bank statement, Invoice (from someone), Purchase order, Receipt, Medical result,
  Vehicle title, Recipe… Each kind has a one-line description the model sees, the **fields** to
  pull (date always; invoice number, amount, account ending as fits), a **folder pattern** and a
  **name pattern**: `Banking/{from}/{date:yyyy}` and `{date:yyyy-MM-dd} {from} statement.pdf`.
  Today's `FilingRule`, grown up. Kinds start from a seeded list of common ones and grow.
  **Everything else** is a real kind too, the catch-all: it files by tag into a general folder, so
  a recipe or a flyer never blocks the inbox while it waits for a kind of its own.
- **From** (who sent it, or whom it's about for a purchase order): one list with alternate spellings
  ("PG&E" = "Pacific Gas and Electric" = "PGE"), so the model's spelling becomes one name. A new one
  is proposed, then made on approval.
- **Tags**: a flat list with alternate names, merge and rename. Many per document: `tax-2026`,
  `house`, `truck`, `medical`, a person's name, a project. The model picks from a shortlist and may
  suggest one new one.
- **Saved on the document**, not the intake review: kind, from, date, title, tags, fields, and where
  each came from (filename, text match, model, person). They follow moves and renames, and the
  Library filters and searches by them.

**A fluid tree** comes from the patterns. Since every document keeps its details, the tree is a
view that can be rebuilt: change a kind's folder pattern ("Banking/{from}/{date:yyyy}" to
"Banking/{date:yyyy}/{from}") or merge two senders, and Stash proposes moving every affected file,
shown as a before/after list you approve. The local model may propose new kinds, senders and tags.
An outside assistant can propose restructuring (new kinds, pattern changes, merges, refiling).
Nothing moves without approval until you've switched the automatic gear on.

## The pipeline for one document

1. **Arrive**: settle, then read text (PDF via `pdftotext`; add `.docx`, which is a zip of XML and
   needs no new tool).
2. **Read the name and the text cheaply (no AI)**: split the filename into words, dates and numbers
   (`INV-20391`, `2026-10-14`, `PO4471`); match known senders and their alternate names in the
   name and the first page; find dates and amounts. A scan's plain name contributes little and
   that's fine. From this, rank the likely kinds, senders and tags.
3. **Ask the model one narrow question**: the filename, the top ~10 kinds with their descriptions,
   the top ~15 senders, the top ~30 tags, the fields of the likely kinds, two or three approved
   examples of the most likely kind, and the first ~3,000 characters. It returns kind (or a proposed
   new one), from, date, title, tags, fields, each with a quote, and how sure it is. About 2,000
   tokens in, `num_ctx` 4096–8192.
4. **Check in code**: quotes are in the text (or the filename); the date parses; the sender maps to
   a known name; tags exist; the kind's required fields are there; the patterns give a valid path
   that doesn't collide (a collision adds " (2)", never overwrites).
5. **Wait for approval, or file**: see below. Either way the result is a filled-in card.
6. **Learn from every approval**: a corrected kind becomes an example for that kind; a corrected
   sender spelling becomes an alternate name; corrected tags train the tag shortlist.

A document can be re-read later (better model, new kinds) and back-filled: documents already in the
Library get details without being moved.

## Approval: the main screen

Approval is where you'll spend your time, so it's the thing to get right.

- **One list of cards**, newest first, each with a thumbnail, and the suggestion as *chips*: kind,
  from, date, title, tags, and the resulting path and name in plain text beneath, updating as you
  change anything. Fields that came from the filename or a sure match look settled; guesses look
  pencilled in.
- **Correct by clicking**: a chip opens a short picker (type to filter, alternate names match, "new
  …" at the bottom). Tags add and remove with a click. The date is typed. You never edit a path.
- **Approve with one key**; approval moves the cursor to the next card. Select several and approve
  them together. "Approve all sure ones" for the cards where nothing was a guess.
- **Preview beside the list**, on the selected card, as the Inbox does now.
- **Skip** leaves a card for later; **Not a document** sends it to trash; **Read again** re-queues it.
- **Undo** is on everything filed, in a "Filed" list with the same cards, for days, not minutes.
- **Trust per kind**: each kind shows its record ("23 approved, 1 corrected"). Its switch, "File
  these without asking", is yours to flip; Stash suggests it after a run of approvals without a
  correction, and turns it back off by itself after you correct one it filed. New kinds, new
  senders and anything unsure always wait. One switch in Settings stops all automatic filing.
- **The safety stays underneath**: the move is the same journaled, recoverable one, checked against
  the file's bytes at the moment it moves. The expiring sign-in-bound tokens can go from the browser
  path (the person clicking is the approval); MCP keeps its stricter proposal rules.

## Outside assistants (MCP)

Kept and pointed at the same model: search and read documents by kind, sender, tag and date;
propose new kinds, senders, tags, merges, pattern changes and refiling; propose corrections to a
document's details. Each proposal lands in the same approval list, labelled with who proposed it.
They can't approve. This is how "ask Claude where my truck title is" and "have ChatGPT tidy up my
tags" work, and how the tree gets bigger reorganizations than a 4B model can plan.

## Keep, change, park

**Keep**: storage and scanning, extraction, the move journal and recovery, previews, the Ollama
queue and evidence check, Settings › AI reader, login and hosting, MCP and OAuth.

**Change**:
- `FilingRule` → **Kind**; the Rules page → Kinds (with each kind's record and trust switch).
- The organizing prompt → the narrow classify-and-extract prompt; no folder ids.
- Add senders, tags and saved document details (one migration); the filename/text pre-reader;
  `.docx` text; Library filters by kind, sender, tag and date.
- Bind a suggestion to the document's bytes and the revisions of the kinds/senders/tags it used,
  not to every folder in the root.
- The Inbox becomes the approval list above. The current single-document slip folds into it.
- Pattern changes and merges produce refiling proposals.
- MCP tools for the new model, landing in the approval list.

**Park** (left working, no new work): document Q&A conversations, folder planning, instruction
conversations and guidance versions, the planned workbench (the approval list replaces it) and PDF
tools.

## Build order

1. **Details on documents**: kinds (from today's rules, plus a seeded starter list), senders, tags,
   saved details, Library filters. Useful with no AI at all.
2. **The approval list** with click-to-correct, one-key approve, batch approve, the Filed list and
   undo, working on hand-entered details first.
3. **Cheap reading**: filename parsing, sender and date matching, `.docx`.
4. **The narrow prompt** and the code checks. Measure on synthetic documents of the main kinds
   (bank statement, invoice, purchase order, receipt, and a few odd ones): kind accuracy, field
   accuracy, seconds per document on the OptiPlex.
5. **Trust**: per-kind record, the switch, the automatic gear, and the master off switch.
6. **Fluid tree**: refiling proposals from pattern changes and merges; back-fill of the existing
   Library.
7. **MCP** tools for kinds, senders, tags, details and refiling.

## Settled with Alex (5 Nov 2026)

- Scanned documents always need naming and filing; emailed ones keep their good names unless
  renamed. The AI may make new folders and move documents (through approval until trusted).

- Scans have plain names; emailed documents' names are useful data and are read first.
- Everything goes in; mostly bank statements, invoices and purchase orders.
- The tree is fluid; the machine may propose changes to it; outside assistants over MCP stay.
- Approval for everything until a kind is trusted, then switchable per kind.
- Hardware: OptiPlex 7090 mini tower, i7, ~16 GB, CPU only; `qwen3:4b-instruct`. Slow is fine.

## Still open

1. The starter list of kinds and the top-level folders they file under (a first draft can come from
   the folders that already exist).
2. Do invoices and purchase orders split into "to me" and "from me"? (A shop's own POs and
   invoices may already live in Beans.)
3. Should "Everything else" go to one folder with tags, or a folder per tag?
