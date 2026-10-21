# Verification

## Release 1.9.4 — October 21, 2026

Release build: zero warnings and errors. **50/50 Core groups and 34/34 WPF groups passed.** The Windows workflow runs both suites again for the release commit. Distribution uses the executable from that successful workflow; its version, archive entries and SHA256 hashes are checked before publication.

The refreshed visual guide uses actual application controls, synthetic English posts and three photographs supplied for publication. The long feed and scrolling frames share the same controls and content. Separate Memo and Review images show the independent streams. The original photographs, renderer's example library and frame sequence are not distributed. All three full-resolution source photographs passed the normal image-import path during rendering. The release build and all exported images were inspected separately from the regression results.

The application diagnostics identified three rejected imports with `The JPEG image is incomplete` from Core validation inside the original-byte fast path. An independent synthetic regression reproduced that exception in the previous 1.9.3 binary: WIC decoded every pixel of a JPEG with trailing bytes, while strict asset validation rejected its source container. The import boundary now normalizes readable source pixels when direct portable storage is unsuitable, retaining strict Core validation and the fast path for already valid PNG/JPEG.

Eight real WIC-readable variants exercise JPEG trailing padding/metadata, duplicate EOI, concatenated JPEG, missing terminal EOI, PNG trailing bytes, duplicate IEND and an ignored ancillary CRC. Checks compare every decoded pixel, retain original files and input arrays, persist notes, reopen assets and create uncached previews. A later unreadable selection still rejects the whole decode batch before asset writes.

Clipboard regressions cover empty, damaged, disposed, nonseekable, failing and unsupported PNG payloads; provider failures during format probing/retrieval; usable Bitmap alternatives; PNG priority; mutable surfaces and lazy streams; retained EXIF orientation; mixed non-image file drops; and unchanged size, pixel and attachment limits. Storage is outside representation fallback and is attempted once.

Failure checks distinguish source access, decoder availability, damaged pixels, empty files, missing stored assets, Log storage, memory and disk exhaustion. Real locked-file and occupied-asset-directory cases preserve sources and allow retry. Preview failures retain references and log their cause. A successful inline image retry clears its obsolete input error without committing another edit. The existing codec, lifecycle, ownership, deletion, backup and restoration suites also passed.

Genuinely unreadable pixel data and unavailable optional Windows codecs remain explicit failure cases. Codec classifications follow [Microsoft's WIC error definitions](https://learn.microsoft.com/en-us/windows/win32/wic/-wic-codec-error-codes); format availability follows [the installed Windows codecs](https://learn.microsoft.com/en-us/windows/win32/wic/native-wic-codecs). These component checks do not claim that every image format or damaged file can be recovered. All synthetic fixtures and diagnostic evidence stay outside the source tree.

## Local 1.9.3 candidate — October 21, 2026

The local interaction review completed with a zero-warning, zero-error Release build, **50/50 Core groups** and **31/31 WPF groups**. GitHub and the published release remain unchanged.

The review covered editing and formatting; posting and draft ownership; Notes/Memo/Review transitions; search and tag filtering; viewport review recording; picture imports and gallery navigation; profile changes; fixed-step width and window commands; backup/restore; error recovery and close. Existing storage and media suites were rerun against isolated synthetic libraries.

The native window menu now shows only state-appropriate commands, including a vertical resize action. Checks verify menu contents, enabled actions, default commands, repeated popup initialization, preservation of extension entries, fixed-width styling, blocked maximize, and native Close reaching cancellable WPF Closing. Native checks create an unshown, test-owned HWND; they do not control a user's desktop window or claim to validate interactive dragging.

Completed-content checks verify that accepted saves and deletions update tag completion and active filters after the UI transition, without rebuilding other editors or losing scroll. Failed saves and cancelled edits emit no completion event. Old controls are detached on feed replacement. Rejected searches restore the previously applied query and indicator; retrying after a real file lock is released saves the edit and applies the new query. A removed explicit Review tag remains an empty filter rather than silently selecting every note.

Profile picture imports now use the background importer. Controlled asynchronous checks cover action availability, name editing during import, duplicate-request rejection, independent candidate ownership, explicit save, decoder failure, picker cancellation and deferred Cancel after both success and failure. The existing durable profile remains unchanged until accepted Save.

These checks establish component and persistence behavior; actual desktop mouse/keyboard operation remains outside this run. No personal notebook was used as test input.

## Local 1.9.2 candidate — October 21, 2026

This candidate has not been published to GitHub. The Release solution build completed with zero warnings and zero errors; all **50 Core check groups** and **28 WPF component check groups** passed against isolated synthetic libraries.

New editor checks exercise WPF's native Space/ShiftSpace commands and queued TextInput, immediate Return or save before idle, ordered-list continuation and exit, Shift+Return within an item, and capture/reload of native line breaks. Input completion preserves selection, caret, native Undo, pending emphasis, text color and highlight. The insertion-format test includes an unflushed baseline with the same input language as its offscreen host.

Gallery checks cover the common 308-DIP image height, proportional widths independent of attachment count and window width, circular navigation, and paging through a single panorama. Layout checks verify that column side lines continue through the bottom gutter. Gallery and Memo illustrations were rendered from application controls and visually inspected outside the repository.

Image checks cover full decoding, original PNG/JPEG byte preservation, EXIF orientations 1–8, 65 codec/pixel-format combinations, bounded frozen preview caching, cold loads, and source-file preservation. Async lifecycle checks cover successful and failed imports, continued composer typing, blocked navigation and saves while assets are pending, inline edit ownership, and deferred close followed by durable persistence.

A same-machine synthetic JPEG benchmark measured import plus first preview at 650 to 168 ms for a normal-orientation 6 MP image and 1,620 to 224 ms for 12 MP. Rotated or converted images still require encoding; their processing time remains comparable, while work runs off the UI thread. These measurements are local examples, not a guarantee for every codec or machine.

WPF checks and illustrations run offscreen. Interactive desktop keyboard testing was not completed because the desktop-control tool did not grant access to ClearNote. The local candidate uses its own Log folder and does not replace the installed application during preparation.

## Published 1.9.1 — October 20, 2026

### Local checks

The Release solution build completed with zero warnings and zero errors. All **50 Core check groups** and **26 WPF component check groups** passed. Checks run only against newly created synthetic data directories.

Core checks cover schema 1–5 compatibility, independent copies, validation, atomic saves and interrupted operations, recovery snapshots, immutable image assets, deletion ownership, backup and restore, portable paths, two independent collection drafts, local profiles and review scheduling. No storage-format change is introduced.

WPF checks cover text, native lists, Undo/Redo, Markdown entry, colors and highlights, image import/paste, gallery navigation and proportions, scoped Notes/Memo behavior, tag completion, profile updates, review visibility, compact editing, readable colors and error recovery. Image checks include 65 encoder/pixel-format combinations and EXIF orientations 1–8. Optional formats still require their Windows codecs. Compact gallery checks cover single and multiple images, narrow and wide columns, extreme aspect ratios, small-source sizing and scrolling bounds. Avatar checks cover the larger presentation, both profile menu actions, keyboard focus and preservation of an active inline edit.

Width checks verify an exact multiplication by 1.07 or 0.93 for each accepted action. Actions at the minimum or screen boundary are disabled rather than applying a partial step. The initial width remains 678 DIPs. The selected width is written atomically to Log/window.json only after an accepted change. Checks also cover restart, smaller screens, damaged, occupied or externally changed settings, and preservation of drafts, inline edits, search, scroll and review state. Percentage values are absent from the menu. Startup-height checks cover ordinary, short, scaled and negative-coordinate monitor work areas and keep equal top/bottom space without changing saved width preferences.

### Visual assets and examples

The black-and-white alien mark uses matching geometry in the WPF avatar, SVG and multiresolution ICO. All ten ICO frames, from 16 to 256 pixels, decode and have the expected dimensions. Small-size and enlarged previews were inspected.

The 14-image Screenshots guide is rendered by `GuideRenderer` from application controls and a new example library. Overview and feed images use a 662-DIP reading column; focused figures crop the relevant control. Images render at twice their logical size for clear text. Popup children and menus are composed with their surrounding controls, while the hover comparison applies the application's hover paint offscreen. These are control illustrations rather than desktop captures.

Regenerate the guide on Windows after building the solution:

```powershell
dotnet run --project src/Folio.Tests/Folio.UiTests/Folio.UiTests.csproj -c Release --no-build -- src/work/checks --render-guide --output docs/images/guide
```

Review every generated figure for complete controls, legible labels and agreement with the README before committing the PNG files. Example libraries and check output stay outside the documentation assets.

### Distribution

The portable Windows x64 executable requires the .NET 10 Desktop Runtime. The portable archive contains exactly ClearNote.exe, README.md and LICENSE. The source archive includes maintainable source, documentation and project metadata; it excludes Log, local notes, imported media, drafts, diagnostics, backups, build output and credentials.

Release preparation compares archive entries and SHA256 hashes. Installation checks the full source manifest, approved destinations, links, locks and original hashes before writing. It preserves the installed Log folder and compares its entire file inventory before and after updating. These checks are release requirements; a successful local build does not by itself establish deployment success.

The Windows workflow builds the solution, runs both synthetic suites and produces an executable artifact. A remote workflow result must be checked on GitHub separately; local checks do not imply that CI has run.
