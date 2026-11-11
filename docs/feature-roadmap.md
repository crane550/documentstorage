# Feature priorities

Replaced on 5 Nov 2026 by [the direction](direction.md): the inbox-to-folder loop comes first.

- [x] **Details on documents:** kinds, senders, tags, saved details, Library search by them.
- [x] **The approval list:** click-to-correct slip, live destination, Enter to file, batch approve, Filed list with Undo.
- [x] **Cheap reading:** filename and first-page hints, Word (.docx) text.
- [x] **The narrow prompt** for a small local model, checked in code.
- [x] **Trust:** per-kind record, file-without-asking switch, master switch.
- [x] **Fluid tree:** refile documents when a kind's pattern changes or senders merge; back-fill details for the existing Library.
- [x] **MCP:** tools for kinds, senders, tags, details and refiling, landing in the approval list.
- [x] **Regulars:** recurring documents fill in from memory without the model, and a kind from one sender can be trusted to file itself.
- [ ] Fold older saved descriptions fully into details (one set of tags); today they only pre-fill cards.
- [x] **Reading rebuilt around the kind** (10 Nov 2026): asked openly, matched by what its name led to before, new kinds proposed, corrections learned. See [Reading](reading.md).
- [ ] Measure the real model on the server with `tests/DocumentStorage.ReaderEval` (kinds right, new kinds found, seconds per document) and tune the questions.
- [ ] The rest of the plan in [Reading](reading.md): details by narrow questions, corrections of details, more for the reader to see, taking out the older pipeline.

Parked (left working, no new work): document conversations, folder planning, instruction conversations, PDF tools.
See [Sorting](sorting.md).
