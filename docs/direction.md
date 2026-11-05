# Direction: the scanner-to-folder loop (proposal, 5 Nov 2026)

A proposal for decision, not yet accepted. When it is, it replaces the "Next feature work" in
[product-plan.md](product-plan.md) and the priorities in [feature-roadmap.md](feature-roadmap.md).

## What Stash is for

> The scanner drops an OCR'd PDF in a folder. Stash reads it, works out what it is, tags it, names
> it, files it in the right folder, and keeps it findable. You look only at what it isn't sure about.

Everything else (asking documents questions, folder planning, assistants over MCP, PDF tools) is
secondary until that loop runs with little supervision on the real scanner output.

## What we can reasonably expect from a local model

Assume a mid-size local model (qwen3 8B/14B class) on the server's hardware: slow, but fine at
narrow questions with a short list of answers.

| Job | Expect | Who should do it |
|---|---|---|
| "Which of these 20 kinds of document is this?" | Good | Model |
| Pull the date, who it's from, an amount, an account ending | Good, when checked against the text | Model, verified by code |
| Pick tags from an existing list | Good | Model |
| Suggest one new tag or kind when nothing fits | Fair | Model, a person confirms once |
| A short human title ("Water bill", "W-2") | Good | Model |
| Choose among 120 folders by id | Poor and slow | Code, from the kind |
| Compose a consistent filename | Inconsistent | Code, from a pattern |
| Design a folder tree, answer open questions over the library | Poor and slow | Park |

The model should **classify and extract**; **code should decide the folder and the name**. That is
the single biggest change this proposes.

## Where the current code stands

Kept as is: storage roots, scanning and settle checks, `pdftotext` extraction (ScanSnap's OCR is
read, not redone), the journaled move with recovery, previews, the Inbox and filing slip, the
durable one-at-a-time Ollama queue, and evidence checking (a fact whose quote isn't in the text is
dropped). These are on the right track.

Not on the right track for this goal:

1. **The AI path asks the model to do everything at once** (`OrganizationProtocol.cs`): invent a
   type name, invent fact names, choose a folder GUID from up to 120 (all of them in the JSON
   schema's enum), compose a filename, explain why. Types and facts are free-form, so
   "bank_statement" today is "statement" tomorrow, and nothing downstream can rely on them.
2. **AI suggestions and rules don't meet.** Rules need a fixed type and fixed fact names; the AI
   invents its own. So a rule can never fire on the AI's reading, and there is no path from "it read
   it right" to "file it without me".
3. **Nothing is kept after filing.** Type and facts live on the intake review and the job. A filed
   document is a name, a path and its text; search is `ILIKE` on those. There are no tags, no
   document date, no "who it's from".
4. **The incoming filename is treated only as untrusted text**, never parsed. The scanner's name
   (scan time, profile name) and the inbox subfolder are free, reliable data.
5. **Any folder or guidance change invalidates every waiting suggestion** (the context hash covers
   every folder in the root), so with a slow model one rename means re-reading the whole inbox.
6. **Speed is left on the table** (`OllamaClient.IdentifyAsync`): qwen3 thinks by default and
   `think` isn't set to false; 12,000 characters of text plus 16,000 of folder list go in each
   request at `num_ctx` 16384. Classification rarely needs more than the first page or two.
7. **Unattended filing is ruled out by design**, and 15-minute, actor-bound, context-hashed
   approvals are the only way bytes move. Right for proposals, wrong as the only gear.
8. **Scope has run ahead of the core loop**: conversations, folder planning, guidance versions,
   OAuth MCP, browser updates, and next on the roadmap a workbench and PDF tools.

## The proposed model

### The things Stash knows

- **Kind** (document type): Water bill, Bank statement, Pay stub, Medical EOB, Receipt, Tax form…
  Each kind has a description the model sees, the **fields** it should pull (date is always one),
  a **folder pattern** and a **name pattern**:
  `Bills/{from}/{date:yyyy}` and `{date:yyyy-MM-dd} {from} {title}.pdf`.
  This is today's `FilingRule`, grown up: one per kind, no match conditions needed for the common
  case (conditions stay possible for "Chase checking vs Chase card").
- **From** (correspondent): who sent it. A list with aliases ("PG&E", "Pacific Gas and Electric",
  "PGE") so the model's spelling is normalized to one name. New ones are proposed, not created
  silently.
- **Tags**: a flat vocabulary with aliases, merge and rename. Many per document. Examples: `tax-2026`,
  `house`, `car`, `medical`, `warranty`, a person's name. The model picks from the list and may
  propose at most one new tag per document.
- **Document fields** saved on the document itself, not on the intake review: kind, from, date,
  title, tags, the kind's fields, and where each came from (filename, model, person). These stay
  after filing, follow moves, and are what the Library filters and searches by.

### The pipeline for one document

1. **Arrive**: the scanner writes the PDF; Stash waits for it to settle and reads its text (today).
2. **Read the name and place (no AI)**: parse the filename for a date/time and words; the inbox
   subfolder can carry a default kind or tags ("Inbox/Receipts" → kind Receipt). Find dates,
   amounts and known "from" names in the text by plain matching. This alone often decides the kind.
3. **Ask the model one narrow question**: the kinds (name + one-line description), the "from" list,
   the tag list, the kind's fields, a few recent approved examples of the likely kind, and the
   first ~4,000 characters. It returns kind, from, date, title, tags, fields, each with a verbatim
   quote, and a 0–1 confidence. `think: false`, small context. No folder ids, no filenames.
4. **Check in code**: quotes found in the text; date parses; "from" maps to a known name or alias;
   tags exist; the kind's required fields are present; the name and folder patterns produce a
   valid, non-colliding path.
5. **File or ask**:
   - Everything checks out, the kind is marked **automatic**, and confidence is over its threshold:
     file it through the existing journaled move, with an **Undo** in a "Filed today" list.
   - Otherwise it waits in the Inbox with the slip already filled in. Correcting it and approving
     teaches: the correction becomes an example for that kind, an alias for that "from".
   - A new kind or "from": the first one always waits; approving creates it.
6. **Kinds earn automatic**: a kind is switched to automatic by hand, and Stash suggests it after,
   say, 10 approvals in a row with no corrections. Any correction resets the count.

What a person sees day to day: a short "Needs a look" list, a "Filed today" list with undo, and a
Library they can filter by kind, from, tag and date.

## Keep, change, park

**Keep**: storage and scanning, extraction, the move journal and recovery, previews, the Inbox and
slip, the Ollama queue and evidence check, Settings › AI reader, login and hosting.

**Change**:
- `FilingRule` becomes **Kind** (fields, folder pattern with folder creation along the pattern,
  name pattern, automatic switch). The rules page becomes the kinds page.
- The organizing prompt becomes the narrow classify-and-extract prompt; drop folder ids from it.
- Add correspondents, tags and saved document fields (one migration), a filename/inbox-folder
  pre-parser, and Library filters.
- Bind a suggestion to the document's bytes and the kind's revision, not to every folder in the
  root, so unrelated edits don't throw away slow readings.
- Add the automatic gear behind a per-kind switch, with undo and a daily list. The approval tokens
  stay for anything a person approves.
- `think: false`, a smaller excerpt and context; show seconds per document in Recent reading.

**Park** (left working, no new work): document conversations, folder planning, instruction
conversations and guidance versions, MCP expansion, the Inbox workbench, PDF tools.

## Build order

1. Saved document fields, kinds (from today's rules), correspondents, tags; the Library filters.
   Useful even with no AI: a person can tag and file by hand.
2. Filename and inbox-folder pre-parse.
3. The narrow prompt, the code checks, the speed settings. Measure on a set of synthetic scans of
   the real kinds: kind accuracy, field accuracy, seconds per document.
4. The automatic gear: per-kind switch, confidence threshold, undo, Filed today.
5. Back-fill: read documents already in the Library to give them kind, from, date and tags,
   without moving them.

## Questions for Alex

1. What does the scanner name files today, and does it scan into more than one folder (profiles)?
2. Roughly which kinds make up most of the pile? A list of 10–20 is enough to start.
3. Should the folder tree follow the kinds (`Bills/PG&E/2026`) or does an existing tree need to be
   kept? Patterns can match either.
4. Is "file automatically, show me what was filed, let me undo" acceptable for kinds you've
   switched on, or should everything always wait for a click?
5. Which model and hardware exactly (GPU or CPU, memory)? It decides the excerpt size and whether
   14B is worth it over 8B.
