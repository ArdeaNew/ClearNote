# Verification

ClearNote 1.9.4.

## Checks

- Release build: zero warnings and errors.
- Core: **50/50** check groups passed.
- Windows UI: **34/34** check groups passed.

## Coverage

Core checks cover saved formats, atomic writes, drafts, profiles, image ownership, deletion, recovery, backup and restore.

UI checks cover editing, lists, formatting, Notes/Memo separation, search, tags, spaced review, galleries, profile changes, window commands and pending-import handling.

Image checks include 65 codec/pixel-format combinations, eight EXIF orientations, eight JPEG/PNG container regressions, clipboard alternatives, source preservation, cold previews and failure recovery.

## Visual examples

The long image and scrolling animation render the application's own feed controls. Memo and Review use the same interface and example content.

## Release validation

Publish the executable from a successful Windows workflow for the exact tagged commit. Check its product and file version, archive contents and SHA256 hashes before attaching the release.

The portable archive contains only `ClearNote.exe`, `README.md` and `LICENSE`. Test libraries, diagnostics, original photographs and build intermediates are excluded.
