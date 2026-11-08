# Images and local OCR

Stash previews and reads common raster images locally. OCR is text recognition, not a vision model: it gives search and Ollama the printed words in a scan/photo. It does not describe a photograph or promise reliable handwriting recognition. Clear, evenly lit, sharply focused text works best. Keep the original visible while verifying a reading.

## Formats

| Files | Preview | OCR |
| --- | --- | --- |
| JPEG: `.jpg`, `.jpeg`, `.jpe`, `.jfif` | Original browser image | Printed text, EXIF and scan orientation handling |
| PNG, BMP | Original browser image | Printed text |
| GIF, WebP, AVIF | Original browser image, decoder permitting | First frame only |
| TIFF: `.tif`, `.tiff` | In-app multipage viewer | First 20 pages with page separators |
| HEIC / HEIF | Server-decoded PNG preview | First/primary image |
| DIB, ICO, PBM, PGM, PPM | Server-decoded PNG preview | First frame/icon size |

TIFF pages have previous/next and a page-number control. All converted previews keep zoom, fit, rotation, expansion and original download. Preview rotation never rewrites the file. SVG/HTML remain escaped source; this module never executes or rasterizes them. Camera RAW, PSD, XCF and other unlisted image formats remain unsupported.

Image decoding uses pinned Magick.NET 15.1.0 and its bundled native libraries. The server needs no separate ImageMagick installation or Windows photo codec. Native runtime files and upstream license notices ship with the app; HEIC *decoding* is included, HEIC encoding is not required.

## Windows server setup

Update Stash, then run once in elevated PowerShell on the server:

```powershell
winget install --id UB-Mannheim.TesseractOCR --exact --scope machine --accept-source-agreements --accept-package-agreements
Set-Location C:\Apps\DocumentStorage\repo
powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy\setup-ocr.ps1
```

The setup script checks the installed tool/language data and Poppler PDF tools, grants LocalService read/execute access to the dedicated Tesseract and Poppler bin directories, preserves existing private instance settings, records the executable/language, and restarts only DocumentStorage. It does not display configuration or passwords. Its real Windows service/ACL behavior needs server validation; automated checks parse it on Linux.

In **Settings → File formats**, select **Check OCR engine**. It reports service-visible availability, version, configured language and orientation support. Existing waiting image jobs retry automatically. Already saved images gain OCR after the normal storage scan notices their new extraction module version; no re-upload is needed.

Default language is `eng`. The `osd` data enables orientation detection for sideways scans; EXIF orientation is always handled. For other languages, install their Tesseract trained data and use `setup-ocr.ps1 -Language eng+spa`, or set `Documents:OcrLanguage` in protected instance configuration. A custom installation uses `-TesseractCommand`. The default machine install path is also auto-detected when no command is configured. Linux uses `tesseract` on PATH with the configured language and optional `osd` data installed.

## Saved work and quality

Images use the same persistent extraction queue as PDFs and Office documents, one file at a time, outside the filesystem writer gate. Tesseract uses at most two OpenMP threads. Decoding has its own single slot shared with converted previews, and releases it before OCR. Ollama retains its existing separate single inference slot.

The decoder normalizes a temporary copy: EXIF orientation, white background for transparency, sRGB/grayscale, bounded resizing and deskewing. With `osd` available, sideways scans are detected and rotated before recognition. Original bytes, names and folders are unchanged. Private temporary PNGs are deleted after success, cancellation or failure. A forcibly terminated process may leave a directory in the service account's temp folder; it is never part of document storage.

Saved text, page numbers, applied scan rotations and mean word confidence are bound to source bytes and extraction/language version. Revisiting a page or scanning unchanged storage does not rerun OCR. **Rescan text** / **Extract text again** explicitly requests it. **Read again with AI** is a separate operation; saved human corrections remain.

The filing slip flags partial extraction and mean page confidence below 65, and keeps those cards from unattended filing. That threshold is a practical verification hint, not a measured probability of correctness. A person can still correct/approve the document normally. Blank images report **No text found**. Missing tools or configured languages report **OCR tool unavailable**, preserve existing text and wait without exhausting retry attempts. Damaged, mislabeled or over-limit files fail explicitly and remain downloadable.

## Limits and verification

Input is limited to 100 MiB, 40 megapixels per processed frame, 16,000 pixels per side, and 256 decoded frame descriptors. TIFF preview/OCR uses up to 20 pages. OCR input is reduced to fit 4,200 pixels per side; converted previews fit 2,400. Native image memory is capped at 512 MiB with disk pixel caching disabled, two native worker threads, and allowlisted raster coders; external delegates/filters/indirect inputs are disabled. Recognition retains at most 500,000 characters and has a 75-second document budget. If later pages time out, completed pages are saved and flagged as partial. Cancelled or stale jobs cannot replace a current result.

The real PostgreSQL/filesystem suite runs actual Tesseract on generated PNG/JPEG/BMP/GIF/WebP/AVIF/DIB/PNM pages, a synthetic HEVC HEIC fixture, multipage TIFF, EXIF and sideways scans. It checks original-byte preservation, persistence, caching, no-text results, missing-language/tool handling and filing-quality notices. `--images-only` runs that subset against a disposable `*_checks` database. Authentication checks cover converted PNG data, page bounds and anonymous/stale denial. `node tests/browser/images.mjs` verifies actual rendered pixels, TIFF navigation, HEIC, saved OCR search/details, engine checks and phone layout. Model classification quality still depends on the chosen Ollama model and real source quality.

Scanned and mixed PDFs now reuse this OCR engine page by page. See [Scanned PDF OCR](pdf-ocr.md) for the PDF-specific workflow, Poppler tool checks and limits.
