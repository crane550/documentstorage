# Scanned PDF OCR

PDFs now use page-aware text preparation. Stash retains usable embedded text and runs local OCR on scanned/text-poor pages. A text cover sheet does not hide scanned pages later in the same file. Page separators are kept so search/conversation evidence still points to the correct original PDF page, including after blank pages.

## Workflow

Files use the existing persistent, single-file extraction queue. Discover/upload the PDF, wait for text preparation, then use the existing AI reader or automatic reading setting. Text extraction and local-model interpretation are separate operations. Opening the PDF preview never triggers OCR or Ollama. Viewing position and rotation survive background extraction updates.

Saved results include page source (embedded text or OCR), character count, OCR word score, scan rotation and partial/failure information. In Library document details, **How text was read** shows this information beside **Extracted text**. Word scores are verification hints, not a measured probability of correct document facts. Partial or low-confidence OCR stays out of unattended filing, and saved AI attempts retain the incomplete-source flag.

The original PDF is never modified. Search and Ollama use the saved PostgreSQL extraction; the browser preview/download continues to show the original file. This does **not** add a selectable OCR text layer to the PDF itself. Handwriting and photo understanding remain outside this printed-text engine.

Unchanged files reuse the saved result across navigation and restarts. This upgrade queues old PDFs once for the new module version. Native text retains Poppler's original text/page-separator convention, so an unchanged text layer does not invalidate saved AI readings just because the module changed. Newly recovered OCR that changes the text supersedes old AI suggestions for the same file; explicitly read again when needed. Human corrections remain saved.

Use Inbox **Rescan text** or Settings → File formats → **Extract text again** to retry. Existing usable text layers remain preferred on retries; this is not a force-OCR-over-every-text-layer mode.

## Tools and Windows setup

PDF OCR uses the already installed Poppler package (`pdftotext.exe`, `pdfinfo.exe`, `pdftoppm.exe`) and the local Tesseract engine introduced for images. No new NuGet dependency or database migration is required. By default the two additional PDF tools are found beside `Documents:PdfTextCommand`, including a Poppler installation under WinGet. Optional `Documents:PdfInfoCommand` and `Documents:PdfRasterCommand` override their paths.

After updating, if Tesseract is already configured, check both **Check OCR engine** and **Check PDF tools** in Settings → File formats. If permissions/tools need fixing, run the existing helper in elevated PowerShell:

```powershell
Set-Location C:\Apps\DocumentStorage\repo
powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy\setup-ocr.ps1
```

The helper now checks all three PDF tools, grants the existing LocalService account read/execute access to the dedicated Poppler bin directory as well as Tesseract, preserves private instance settings and restarts only DocumentStorage. Windows ACL/service behavior still needs verification on the actual server. See [Image OCR](image-ocr.md) for first-time Tesseract installation and language setup.

Missing text tools report **PDF text tool unavailable**. Missing page metadata/rendering tools report **PDF OCR tools unavailable**. Missing Tesseract/language data reports **OCR tool unavailable**. These wait for recovery without spending retry attempts or erasing an existing saved index. PDFs with useful text on every processed page do not need Tesseract or page-rendering tools. PDFs at the page-count boundary also need metadata to establish whether extraction is partial.

Password-protected PDFs report **PDF password required**. The browser's preview password remains browser-only; OCR does not receive or save it. Corrupt PDFs fail explicitly. A later page failure/time limit retains completed OCR and available embedded text and marks the result partial.

## Bounds and current limits

PDF input is limited to 250 MiB. Text preparation covers the first 100 PDF pages and keeps up to 500,000 characters. Up to 20 scanned/text-poor pages receive OCR within a 75-second document budget; remaining native text is retained and unread pages are marked. Long native PDFs disclose the omitted page count. A page with fewer than 32 letters/digits is considered text-poor, so a page number or short footer does not prevent OCR. This is a practical heuristic, not layout/vision understanding; an existing bad but substantial OCR layer may still need external repair.

Poppler renders one selected page at a time to a private temporary PNG, with its longest side limited to 4,200 pixels and a 20-second rendering timeout. Image normalization, deskewing, orientation recognition and Tesseract are shared with image OCR; they retain existing memory/thread/output limits. Subprocesses use argument lists, no shell, bounded output and cancellation with process-tree termination. Source bytes, revision and module/language version are revalidated by the existing worker before results can replace saved text. Original filesystem changes still use the unchanged journaled filing workflow.

Tests use synthetic PDFs and actual Poppler/Tesseract, covering image-only, mixed, footer-only, rotated, blank-intervening pages, native-text fast paths, page limits, caching, tool recovery and confidence flags. `--pdf-ocr-only` runs this subset of the real PostgreSQL/filesystem suite against its disposable catalog. `node tests/browser/pdf-ocr.mjs` verifies actual PDF pixels, unchanged downloads, OCR search/details, mixed-page navigation, tool checks, password UI and phone layout.
