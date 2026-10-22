# Verification

ClearNote 1.9.7.

## Checks

- Release build: zero warnings and errors.
- Core: **54/54** check groups passed.
- Windows UI: **35/35** check groups passed.

## Coverage

Core checks cover saved formats, atomic writes, drafts, profiles, image ownership, deletion, recovery, backup and restore.

UI checks cover editing, lists, formatting, independent feeds and drafts, search, tags, Notes-only review, galleries, profile changes, native height changes, window-menu lifecycle and pending-import handling.

Image checks include 65 codec/pixel-format combinations, eight EXIF orientations, eight JPEG/PNG container regressions, clipboard alternatives, source preservation, cold previews and failure recovery.

## Visual examples

The long image and scrolling animation render the application's own feed controls. Memo, Opinions, Economic Analysis and Review use the same interface and example content.

## Release validation

Publish the executable from a successful Windows workflow for the exact tagged commit. Check its product and file version, archive contents and SHA256 hashes before attaching the release.

The standalone EXE includes its .NET/WPF runtime and native dependencies. Bundle checks reject external framework requirements, missing runtime files and loose dependencies. An isolated startup check runs the published EXE without showing a window and verifies that CoreCLR loads from the bundle.

The portable archive contains only `ClearNote.exe`, `README.md` and `LICENSE`. Test libraries, diagnostics, original photographs and build intermediates are excluded.
