# Synthetic image fixture

`synthetic-statement.heic` contains only the generated Cedar Bank test text from
`tests/browser/image-fixture.py`; it is not a user document. It was encoded using
Pillow with pillow-heif 1.8.1 at quality 85. Magick.NET's bundled HEIC reader is
used in production and tests; its HEVC encoder is deliberately not required.
Regenerate the source PNG with the Python fixture generator, then save it as
HEIF using a development-only pillow-heif installation.
