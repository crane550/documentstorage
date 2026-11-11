# Reading: working out what a document is

Rebuilt on 10 Nov 2026 (Alex: "the purpose of the AI here needs to be to find out what the
document is… it needs to know when to create a new kind… the little rules that help us distinguish
things belong in the learned data, not hard coded in"). This replaces the single sorting prompt
described in older notes. [Sorting](sorting.md) still describes everything after the reading:
the card, the patterns, approval, trust and Tidy up.

## What was wrong

Receipts kept arriving as purchase orders, and anything unusual was squeezed into the nearest kind.
Three things caused it, and none was the model being too small:

1. **The reader was shown the list of kinds first** and told to pick "the best match… if none fits
   at all, a new kind". A small model asked to choose from a list chooses from the list.
2. **Code ranked the kinds by words in common** with each kind's description and field names. A
   store receipt says "order" and "total", so *Purchase order* (described with "order", fields
   "po_number, total") went to the top of the list the reader then chose from.
3. **Nothing about the kind was learned from a correction.** Changing Purchase order to Receipt
   reset a counter. The next receipt was read exactly the same way.

## How a reading works now

A reading is a short run of narrow questions about one document. Code decides what to ask next and
checks every answer; the model never decides what to do. (`DocumentReading.RunAsync`, pure: no
database.)

| Step | Who | What happens |
|---|---|---|
| 1. Describe | Reader | "What type of document is this?" asked with **none of your kinds on show**. It answers in its own words ("Store receipt", "Sheet music", "Subscription renewal"), with one sentence on what such documents are and two to four phrases from the document it went by. Phrases not really in the text are dropped. |
| 2. Kind | Code | Where has that name led before? It is one of your kinds by name, or a name the reader has given before to documents you always filed as one kind: that is the kind, and it is **settled**. No model call. |
| 3. Match | Reader | Only when step 2 can't say. A name new to Stash is compared with your kinds (name, description, other names the reader has used for each): which is nearest, and *does this document really belong there?* "No" proposes a **new kind** under the reader's own name. A name that has led to two kinds is settled between just those two, shown one document of each. Either way the kind is pencilled in and waits for you. |
| 4. Details | Reader | Sender, date, title, tags and **that one kind's** fields with their guides, plus up to two earlier documents of the kind with the details you approved. |

Every question starts with the same system prompt and the same document block (file name, the inbox
folder it arrived in, and the text: the start and, for a long document, the end). Only the question
changes, so a local server that keeps what it has read reads the document once per reading.
Whether Ollama does reuse it on the server still has to be timed there (see Measuring).

### What is learned, and nothing else

One pairing, kept on the document when a person approves it: **what the reader called it**
(`DocumentDetail.ReaderWords`) beside **the kind it was filed as**. That is the whole of it.

- The reader calls an order confirmation "Order confirmation" and you file it as Receipt: next
  time those words lead straight to Receipt, settled, with no second question.
- You file something called "Order confirmation" as a Purchase order too: the name now leads to
  two kinds, so it is no longer trusted. The reader is asked to choose between those two, shown one
  of each, and the card waits for you with the other kind one click away.
- The reader calls a receipt "Purchase order" (its own mistake, with no list to blame) and you
  correct it: even a kind's own name is questioned from then on, the same way.
- One stray filing doesn't unsettle a name that nearly always leads to one kind (the same three to
  one that regulars use).

Only documents a person stands behind teach (`DocumentDetail.Confirmed`): approved in the Inbox with
"Remember as an example" ticked, accepted in back-fill, saved in the Library, or "Looks right" on
something that filed itself. A document that filed itself is not evidence until then, so the reader
can't train itself on its own guesses, and undoing a filing takes it back out. Each kind's latest
twelve confirmed documents are what a reading looks at, so what it knows follows how you file now.

There is no table of rules, no list of telling words per kind, and nothing about receipts or
purchase orders in the code. Kind descriptions are yours to edit (the Match step reads them), and
assistants can propose better ones.

### When a new kind is made

The reader proposes one when its name for the document is new and it says the document doesn't
belong in the nearest kind. The proposal carries the reader's name and its one-sentence description,
and waits: approve it as it is, rename it, or pick one of your kinds instead (and that pairing is
learned). The catch-all ("Everything else") is never offered to the reader; it is your choice, and
if you file sheet music there, "Sheet music" leads there from then on.

### What counts as sure

A card is sure, as before, when nothing on it is a guess. The kind is no longer a guess only when it
was settled in step 2. A kind the reader *chose* (step 3) or a new kind always waits for a person,
so a kind files itself only on names you have already confirmed.

### On the slip

Under Kind: "The reader calls it “Store receipt”", and the other kinds worth one click (the nearest
kind when a new one is proposed; the other kind when a name has led to two). Correcting the kind
is the teaching; there is nothing else to tick.

Each question asked and its answer is kept with the reading; assistants can see them
(`read_reader_attempt`, section `context`; `input` is the document block), along with
`reader_called_it` and `other_kinds` on each Inbox card.

## What the reader is given

| | Describe | Match | Details |
|---|---|---|---|
| File name, inbox folder, text (start and end, 6,000 characters) | ✓ | ✓ | ✓ |
| Your general guidance (800 characters) | ✓ | | ✓ |
| Your kinds: name, description, other names used for each | | ✓ | |
| One confirmed document of each kind in question | | when a name led to two | |
| The kind's fields and how each is found | | | ✓ |
| Known senders (25) and tags (30), likeliest first | | | ✓ |
| Two earlier documents of the kind with approved details, and assistants' lessons on them | | | ✓ |

Gone from the prompt: the list of every kind with every kind's fields, the three "approved examples"
picked by word overlap (they carried no text), and the kind ranking by description words.
`DocumentHints` still finds senders, tags and dates without the model, and takes a kind only from
the file's own name ("Acme_Invoice_INV-20391.pdf"), never from stray words in the text.

## Tried and thrown away

Before settling on names, the rebuild compared documents by the words they share, to show the
reader "similar ones you filed" and to take the kind of a near-identical document without asking.
On the synthetic set it failed in the way that matters: a purchase order and the supplier's invoice
for it share nearly every word (0.41–0.43 alike) while receipts from different shops share almost
none (0.10 at best), and the nearest document was the same type for only 15 of 23. Shared words
find the same sender or the same deal, not the same type of document. Showing those as examples
would have caused the receipt/purchase-order mix-up rather than cured it, so none of it was kept.
(Regulars already cover "the same thing from the same sender".)

## Measuring it

`tests/DocumentStorage.ReaderEval` reads 34 synthetic documents with a real model, the way an inbox
would meet them: first one of every type with nothing learned, then the rest after "a person"
approved or corrected the first. It needs no catalog and files nothing.

```
dotnet run --project tests/DocumentStorage.ReaderEval -- --endpoint http://127.0.0.1:11434 --model qwen3:4b-instruct
```

It prints, per document, what the reader called it, the kind arrived at and how, whether that was
right, and the seconds each question took; then how many kinds were right the first time and after
learning, how many documents that fit none of the starter kinds were proposed as new kinds (sheet
music, a user manual, a line card, a subscription renewal, a recipe…), how many that do fit were
wrongly proposed as new, and the names it learned. `--show` prints every question and answer.

Run it for the model on the Dell and for one larger model on the desktop reader before choosing.
The second and third questions should take far less time than the first if the server is reusing
the document; if they don't, say so and the order of the prompt can be looked at again.
**No accuracy figure in this repository comes from a real model yet**: the checks use a scripted
reader and prove the steps, not any model's judgement.

A reading must finish inside the one time limit in Settings › AI reader (default ten minutes) for
all its questions together. On slow hardware raise it.

## The plan from here

In order. Each stage should beat the last run of the measuring tool or be dropped.

1. **Measure on the server** (above) with the current reader and one larger model. Decide the
   reader from the numbers, and tune the three questions' wording against them.
2. **Details the same way as the kind**: narrower questions with code doing the finding. The date
   as a choice among the dates code already finds, each shown with its line; the sender as a choice
   among names found in the text; each field as a choice among values printed beside its words.
   Then one place that decides each detail and whether the card is sure, instead of the order of
   precedence in `CardAsync` (saved details, a regular's memory, the reading, the file name, older
   descriptions, printed words).
3. **Corrections of details teach** as corrections of the kind now do: what the reader said beside
   what you approved, for sender and date rules first.
4. **More for the reader to see**, cheaply: page count and file type, and a PDF's title, author and
   producer (the tool that counts pages already reads them and throws them away).
5. **Take out the older pipeline**: the organizing and fact-only readings, document types, filing
   rules and saved descriptions (`IdentificationService`'s other two modes, `OrganizationProtocol`,
   `OrganizationService`, the Rules page). They are parked but still run, still fill cards and are
   most of the reading code. This deletes working features, so it waits for Alex's go-ahead.
6. **Assistants as coaches** over what is learned: list the names and where they led, propose kind
   descriptions, and review kinds the reader keeps choosing between.

## Checks

`tests/DocumentStorage.Checks/ReadingChecks.cs` (`-- --reading-only` runs it alone): the open
question shows no kinds; a kind's own name needs no second question; a new name is matched and
waits; none fitting proposes a new kind with the nearest kept as an option; a learned name leads
straight to its kind; a name that led to two kinds is chosen between those with one of each shown;
a corrected kind's own name is questioned; one stray filing doesn't unsettle a name; the same
document block for every question; start and end of a long document; and through the catalog:
what approval keeps, that a self-filed document doesn't teach until "Looks right", and that undo
takes a filing back out. `ScriptedReader` answers a reading's questions from one scripted result for
the other checks. `tests/browser/ui.mjs` `answerReading` does the same for the browser stand-ins.
