# In-app document previews

Select a document in the **Library** to open its preview beside the file list. Select an **Inbox** item to keep its content visible beside its filing slip. Opening a preview brings it into view. PDF/image previews render the original; Office previews use saved extracted content. Originals are never modified by preview controls.

## Controls and formats

- **PDF:** scroll through pages, use previous/next or enter a page number, zoom in/out, fit width, rotate, find text across pages (Enter searches; arrows move between matches), and select/copy its text layer. Image-only scans render normally but require existing OCR for text selection/search. Password-protected PDFs prompt inside the viewer; the password is passed only to the local browser worker and is not sent to the server or saved. Static form/annotation appearances render; editing, scripting, active annotation controls and XFA are disabled.
- **Images:** PNG, JPEG, GIF, WebP, BMP and AVIF use the browser's image decoder, with zoom, fit width and rotation. Decoder support depends on the browser. TIFF, HEIC/HEIF, DIB, ICO and PBM/PGM/PPM use server-decoded PNG previews. TIFF has page navigation; other converted images show the first/primary frame. Camera raw conversion is not implemented. See [Image OCR](image-ocr.md).
- **Text/source:** TXT, Markdown, CSV/TSV, JSON, XML, YAML, logs, INI, HTML and SVG show escaped source with a wrap-lines control. HTML/SVG are not active pages/images, and Markdown is not rendered as HTML. UTF-8 and BOM-marked Unicode text are supported. Other encodings use the original-download fallback.
- **Word (.docx):** saved text from the body, headers and footers, with a wrap-lines control. Original page layout and embedded pictures are omitted.
- **Excel (.xlsx):** worksheet tabs and cell tables retaining row/column coordinates. Uses saved formula results; no formula recalculation, original formatting or charts. Preview limits are smaller than indexing limits and are disclosed. See [File formats](file-formats.md) for the full capability matrix and bounds.

**Expand** opens a large in-app viewer. Escape or **Close expanded preview** returns to the workspace; keyboard focus stays in the expanded viewer. **Reload preview** retries a failed load. **Download original** remains available for every type, including unsupported and damaged files. PDFs are limited to 250 MiB and images to 100 MiB for inline loading. Text shows at most 200,000 characters and clearly reports truncation.

Use a current browser supporting ES modules, workers, canvas and CSS scopes. The viewer adapts to narrow/mobile screens. The desktop Inbox keeps the original alongside the filing slip; narrow layouts stack the panels. Zoom/rotation are viewing settings and do not rewrite the original.

## Serving and lifecycle

`/documents/{id}/preview?v=...` streams allowlisted PDF/image types with an inline disposition, byte-range support, nosniff, no-store and a restrictive raw-content CSP. `/documents/{id}/preview-text?v=...` returns bounded source text as JSON. `/documents/{id}/preview-data?v=...` returns saved Office text/artifacts after checking the source fingerprint and extraction version; it reports HTTP 425 while extraction is queued/running. The viewer inserts text using textContent. The app's existing cookie authorization protects all preview endpoints; MCP bearer tokens do not authenticate browser previews. File extensions are checked against binary signatures before inline serving. Office packages cannot be served through the inline binary endpoint. Converted raster images use cookie-protected `/preview-image-info` and `/preview-image` routes, with source signature/revision/root checks, bounded native decoding and explicit PNG output. Conversion runs outside the writer gate and revalidates the entry before publishing.

A preview revision binds the catalog ID, root, relative path, size and timestamp. Every stream request checks the current entry, actual metadata, original root identity, missing/trash status and safe physical path. Ordinary edits/moves invalidate an old URL, avoiding range requests silently mixing normal file revisions. External edits that deliberately retain size/timestamp need the later fingerprint reconciliation milestone; preview versioning is not a content-addressed filesystem snapshot. Rescan/select again after a file changes. Open read handles allow deletion/rename sharing so an active preview does not unnecessarily block app-managed filing. New requests still revalidate the current location.

Switching or closing releases the old loading task, worker, event handlers, observers and image blob URL. PDF.js renders locally with scripting/evaluation disabled and no document cloud service or browser CDN. A corrupt/mislabeled/unavailable file yields a visible retry/download fallback. Office artifacts and the persistent extraction queue use the additive `AddFormatExtractionQueue` migration.

## Assets

PDF.js is pinned to **pdfjs-dist 6.5.438**, Apache-2.0, with its license and bundled font/CMap/WASM licenses retained under `src/DocumentStorage.Web/wwwroot/vendor/pdfjs`. A CSS scope isolates its generic class names from the app's cardboard theme. Support assets are copied at development time and committed, so Windows deployment requires only the existing .NET publish process.

To regenerate with Node installed:

```sh
npm --prefix tools/preview-assets ci --ignore-scripts --omit=optional
npm --prefix tools/preview-assets run sync
```

## Verification

The authentication runner checks anonymous denial, authorized PDF bytes, real 206/416 ranges, inline/encoded filename and security headers, fake-image rejection, bounded escaped text, Windows Unicode text, stale/moved/offline/trashed files, and an open preview handle during a real rename. Run the Release solution build and `tests/DocumentStorage.AuthenticationChecks` against its dedicated disposable catalog.

For browser checks, use a fresh disposable app catalog on loopback port 5244, private bootstrap admin settings, and a key directory dedicated to that catalog. The fixture generator needs Python with reportlab and Pillow; neither is a production dependency.

```sh
python3 tests/browser/preview-fixture.py
# Start the app against that disposable catalog; supply credentials privately.
node tests/browser/preview.mjs
```

Browser tests verify actual canvas ink and filled-form appearances, navigation/zoom/rotation/search, encrypted PDFs, all advertised image formats, escaped text, bad-file fallbacks, fast switching, expanded/mobile viewing, Intake facts alongside the PDF, retained cardboard styling and same-origin assets. Fixtures live only under ignored `.local/`. Existing document, folder and Intake browser workflows also remain part of verification. Actual server/browser deployment still needs the usual Windows update and user validation.
