# Verification

ClearNote 1.9.1 — October 20, 2026.

## Local checks

The Release solution build completed with zero warnings and zero errors. All **50 Core check groups** and **26 WPF component check groups** passed. Checks run only against newly created synthetic data directories.

Core checks cover schema 1–5 compatibility, independent copies, validation, atomic saves and interrupted operations, recovery snapshots, immutable image assets, deletion ownership, backup and restore, portable paths, two independent collection drafts, local profiles and review scheduling. No storage-format change is introduced.

WPF checks cover text, native lists, Undo/Redo, Markdown entry, colors and highlights, image import/paste, gallery navigation and proportions, scoped Notes/Memo behavior, tag completion, profile updates, review visibility, compact editing, readable colors and error recovery. Image checks include 65 encoder/pixel-format combinations and EXIF orientations 1–8. Optional formats still require their Windows codecs. Compact gallery checks cover single and multiple images, narrow and wide columns, extreme aspect ratios, small-source sizing and scrolling bounds. Avatar checks cover the larger presentation, both profile menu actions, keyboard focus and preservation of an active inline edit.

Width checks verify an exact multiplication by 1.07 or 0.93 for each accepted action. Actions at the minimum or screen boundary are disabled rather than applying a partial step. The initial width remains 678 DIPs. The selected width is written atomically to Log/window.json only after an accepted change. Checks also cover restart, smaller screens, damaged, occupied or externally changed settings, and preservation of drafts, inline edits, search, scroll and review state. Percentage values are absent from the menu. Startup-height checks cover ordinary, short, scaled and negative-coordinate monitor work areas and keep equal top/bottom space without changing saved width preferences.

## Visual assets and examples

The black-and-white alien mark uses matching geometry in the WPF avatar, SVG and multiresolution ICO. All ten ICO frames, from 16 to 256 pixels, decode and have the expected dimensions. Small-size and enlarged previews were inspected.

The 14-image Screenshots guide is rendered by `GuideRenderer` from application controls and a new example library. Overview and feed images use a 662-DIP reading column; focused figures crop the relevant control. Images render at twice their logical size for clear text. Popup children and menus are composed with their surrounding controls, while the hover comparison applies the application's hover paint offscreen. These are control illustrations rather than desktop captures.

Regenerate the guide on Windows after building the solution:

```powershell
dotnet run --project src/Folio.Tests/Folio.UiTests/Folio.UiTests.csproj -c Release --no-build -- src/work/checks --render-guide --output docs/images/guide
```

Review every generated figure for complete controls, legible labels and agreement with the README before committing the PNG files. Example libraries and check output stay outside the documentation assets.

## Distribution

The portable Windows x64 executable requires the .NET 10 Desktop Runtime. The portable archive contains exactly ClearNote.exe, README.md and LICENSE. The source archive includes maintainable source, documentation and project metadata; it excludes Log, local notes, imported media, drafts, diagnostics, backups, build output and credentials.

Release preparation compares archive entries and SHA256 hashes. Installation checks the full source manifest, approved destinations, links, locks and original hashes before writing. It preserves the installed Log folder and compares its entire file inventory before and after updating. These checks are release requirements; a successful local build does not by itself establish deployment success.

The Windows workflow builds the solution, runs both synthetic suites and produces an executable artifact. A remote workflow result must be checked on GitHub separately; local checks do not imply that CI has run.
