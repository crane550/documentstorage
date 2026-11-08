# File format modules and extraction

Settings → **File formats** reports the capabilities of the modules actually registered in this app. Preview, text extraction and OCR are separate capabilities. Search and the Ollama reader use saved extracted text; opening a preview does not ask Ollama to interpret the file.

| Files | Text for search / AI | Preview |
| --- | --- | --- |
| PDF | Embedded text plus local OCR for scanned/text-poor pages | Original PDF, including image-only scans |
| Word `.docx` | Body, headers and footers | Extracted text; original layout and embedded pictures are omitted |
| Excel `.xlsx` | Sheet names, cell references, strings, numbers and saved formula results | Worksheet tabs and a bounded cell table |
| TXT, Markdown, CSV/TSV, JSON, XML, YAML, logs, INI | UTF-8 or BOM-marked Unicode text | Escaped source text |
| PNG, JPEG, GIF, WebP, BMP, AVIF | Local Tesseract OCR | Original image, browser decoder permitting |
| TIFF, HEIC/HEIF, DIB, ICO, PBM/PGM/PPM | Local Tesseract OCR; bounded pages/first frame | Converted image; TIFF page navigation |
| HTML, SVG | Filename only | Escaped source; no active HTML or visual SVG rendering |
| Legacy Word `.doc`, Excel `.xls`, other formats | Filename only | Download original |

Office extraction uses the .NET libraries already included in Stash. Neither Microsoft Office nor LibreOffice is required. Encrypted, damaged or overly large packages return a controlled failure; the original remains downloadable. Excel uses saved formula results and never recalculates formulas, executes macros or follows external data relationships. Dates with recognized date styles are normalized. Charts, formatting, merged-cell layout and embedded objects are not rendered. A formula without a saved result is explicitly labeled.

## Saved work and retries

Discovery persists extraction jobs while briefly holding the catalog/filesystem writer gate. A separate background worker runs one extraction at a time **outside** that gate, so converters do not hold up folder operations. It has its own slot, separate from the existing Ollama inference slot. This does not increase Ollama concurrency.

Completed text, truncation flags and normalized preview artifacts are stored in PostgreSQL, bound to the source hash and module version. Unchanged files reuse them across page navigation and restarts. Changed files or a new module version queue replacement extraction. An upgrade from versions without module stamps prepares existing supported files once, in the background. Existing human reviews are retained; old AI interpretations are superseded when replacement text changes for the same source bytes, and require an explicit AI reread.

Running jobs resume after a restart without resetting their attempt budget. Offline or recovery-paused storage waits rather than erasing indexed content. Missing PDF tooling waits for a retry. Transient extraction failures have a three-attempt budget; malformed documents show a terminal failure until explicitly retried or changed. Source metadata, current bytes and module version are rechecked before publishing results, so a moved or replaced document cannot accept a late result.

Use Inbox **Rescan text**, or Settings → **File formats** → **Extract text again**, to request replacement extraction. For image files this runs local pixel OCR; PDFs retain usable embedded text and run OCR for scanned/text-poor pages. It does not ask Ollama for another reading. When text is ready, use the existing **Read again with AI** action if needed. Text preparation and AI interpretation remain distinct stages.

## Module contract

Modules live in `src/DocumentStorage.Core/Formats`. Implement `IDocumentFormatModule` and register it in `DocumentFormatRegistry.CreateDefault` (or supply a registry at composition time). There is no runtime assembly discovery or uploaded plugin execution.

Each module declares stable format IDs, extraction versions, case-insensitive extensions, MIME types, extraction capability, preview presentation and limits. The same registry drives discovery, extraction, preview selection and the capabilities page. The scanner, filing logic and AI reader do not contain format-specific extension lists. Bump the module version when extraction semantics or saved artifact structure changes.

`ReadAsync` returns bounded text, a status, a truncation flag and optional normalized artifact JSON. Treat source contents as untrusted document evidence. Honor cancellation, bound memory/input expansion, avoid external references and return controlled errors. Shared Office package handling bounds ZIP/XML data and prohibits DTD/entity resolution.

Preview kinds are reusable browser presentations: PDF, image, escaped source, document text and worksheet tables. A new format can reuse one without changing the viewer. A genuinely new presentation needs a renderer in `document-content.js` and corresponding UI support. All document values use DOM text nodes, never HTML. Inline binary formats must also supply signature validation. Authorization, storage identity, revision checks and source fingerprints remain in Core/hosting, not individual UI handlers.

Current bounds include 500,000 extracted characters; PDF text covers the first 100 pages with a 30-second tool timeout. Office input is limited to 100 MiB, 10,000 ZIP entries, 200 MiB total uncompressed data and 20 MiB per XML part. Excel indexes at most 100 sheets / 100,000 cells per sheet within the text limit. Its preview retains up to 20 sheets, first 200 rows / 50 columns, 3,000 saved cells and 150,000 cell characters. Text previews display at most 200,000 characters. Character and worksheet truncation is reported; the PDF page cap is fixed. Preview limits can be smaller than indexed content.

## Verification

The real PostgreSQL/filesystem check runner includes a custom format module, version upgrades, unchanged-file caching, single-slot extraction, responsive folder writes, stale results, restart recovery and offline retry. Office fixtures cover rich/shared strings, dates, sparse coordinates, cached/missing formulas, malformed packages, external relationships, DTD rejection and preview/indexing bounds.

Authentication checks cover derived-preview authorization, saved Unicode text, cache headers and same-size/timestamp byte replacement. `node tests/browser/formats.mjs` uses synthetic fixtures generated by Python's standard library against a fresh loopback catalog. It checks worksheet tabs, literal markup, downloads, Word text, search, retries and phone layout. Existing PDF/image preview, sorting, MCP and recovery workflows remain regression checks. No user documents belong in fixtures or commits.

See [Image OCR](image-ocr.md) for engine setup, formats, quality flags, limits and real OCR verification.

See [Scanned PDF OCR](pdf-ocr.md) for mixed PDFs, page provenance, tool setup and bounds.
