<p align="center"><img src="src/Folio.App/Assets/Folio.svg" width="88" alt="ClearNote alien logo"></p>

# ClearNote

An offline notebook for Windows, built around a continuous feed. Capture ideas, keep everyday information close, and revisit learning notes through spaced review.

[Download for Windows](https://github.com/ArdeaNew/ClearNote/releases/latest) · [Install](#run) · [Build from source](#build)

## Features

- **Notes** — write above a scrolling feed, then post with one click or Ctrl+Enter.
- **Memo** — a separate feed for addresses and everyday information, with its own draft and search.
- **Review** — revisit learning notes by tag; a sufficiently visible note records one review per round. Memo stays outside review.
- **Writing** — Markdown headings, emphasis, quotes and lists, with selected text colors and highlights. Code fences remain plain text.
- **Images** — attach up to four pictures in a compact gallery that keeps their natural proportions. Imported files are copied; originals remain unchanged.
- **Your identity** — right-click an avatar to edit your name or change your picture. No account is required.
- **A comfortable window** — start with a tall reading area and choose Wider or Narrower from the menu. ClearNote remembers the width; height remains resizable.

The interface is English, with a white background and dark text. Latin text uses Consolas; Chinese text uses FangSong.

## Screenshots

A composer above a continuous feed. White space, fine dividers and borderless controls keep the notes easy to scan.

<img src="docs/images/guide/overview.png" width="760" alt="ClearNote Notes feed with a composer, readable text, larger avatars and compact pictures">

### Capture a thought

Write at the top and click **Post**, or press **Ctrl+Enter**.

<img src="docs/images/guide/compose.png" width="760" alt="A new thought in the composer with the Post button ready">

### Shape the text, then edit in place

Use Markdown headings and lists. Select text to add a color or highlight with the toolbar.

<img src="docs/images/guide/formatting.png" width="760" alt="Headings, an ordered list, colored text and a highlight in the note editor">

Double-click a post to edit it in the feed. **Save** keeps the changes; **Cancel** restores the saved note.

<img src="docs/images/guide/edit.png" width="760" alt="A post being edited in place with a compact formatting toolbar and Save and Cancel actions">

### Add a tag. Return to the idea.

Type **#** for existing tags in the current feed; **Tab** or **Enter** accepts a suggestion. Open **Review** to choose what to revisit.

| Tag suggestions | Review by tag |
| --- | --- |
| <img src="docs/images/guide/tag-suggestions.png" width="340" alt="Existing hashtag suggestions beside the note being written"> | <img src="docs/images/guide/review-menu.png" width="340" alt="Review menu with All tags and individual learning tags"> |

Scroll through a spaced review feed. A sufficiently visible note records one review per round; everyday Memo posts stay separate.

<img src="docs/images/guide/review.png" width="760" alt="The Review feed focused on a selected learning tag">

### Keep everyday details close

**Memo** has its own feed and draft for reference information that does not need spaced review.

<img src="docs/images/guide/memo.png" width="760" alt="The separate Memo feed with practical reference notes and its own composer">

Open the magnifier to search the current feed. The search field appears only when needed.

<img src="docs/images/guide/search.png" width="760" alt="The compact Search popup and matching notes in the current feed">

### Give pictures their place

Add up to four pictures. The compact gallery keeps their proportions; use its arrows or **Shift+wheel** to browse, or focus the gallery and press **Left/Right**. Imported pictures are copied, leaving the originals unchanged.

<img src="docs/images/guide/gallery.png" width="760" alt="Compact landscape and portrait pictures in a note with horizontal gallery navigation">

### Make it yours

Right-click an avatar for **Edit profile** or **Change picture**. Your chosen name and picture appear throughout the notebook.

| Avatar actions | Local profile |
| --- | --- |
| <img src="docs/images/guide/avatar-menu.png" width="340" alt="A note avatar with the Edit profile and Change picture menu"> | <img src="docs/images/guide/profile.png" width="340" alt="Profile dialog with picture selection and a display name field"> |

### A little feedback. Room to read.

Hover gives the whole post a soft gray background while the text stays dark and readable.

<img src="docs/images/guide/hover.png" width="760" alt="The same post at rest and with its subtle gray hover background">

Choose **Wider** or **Narrower** from the menu. ClearNote remembers the reading width and starts with a tall window fitted to the display.

<img src="docs/images/guide/width.png" width="760" alt="The same note at two reading widths with text wrapping naturally">

## Run

[Download the latest Windows release](https://github.com/ArdeaNew/ClearNote/releases/latest).

Current releases are unsigned. See the [Code signing policy](docs/CODE_SIGNING.md) for signing status and release verification.

Use Windows x64 with the **Microsoft .NET 10 Desktop Runtime** installed. Extract the portable folder and open `ClearNote.exe` from a writable location.

A new notebook starts with two welcome posts. They are ordinary editable notes; deleting them also removes an exclusively owned picture. Deleted welcome posts do not return.

## Everyday controls

- Post: Ctrl+Enter. Save drafts and open edits: Ctrl+S. Refresh: F5.
- Double-click a post to edit. Save commits changes; Cancel or Escape restores the saved post.
- Type `#tag` to label a note. Tab or Enter accepts a tag suggestion.
- At the start of a paragraph, type a positive number followed by a space to begin an ordered list. Enter continues it; Enter on an empty item exits.
- Use Search to filter the active feed. Review opens its tag menu without changing the feed until a choice is made.
- Browse pictures with the gallery arrows, Left/Right keys, or Shift+wheel. The ordinary mouse wheel scrolls the feed.

PNG, JPEG, BMP, GIF and TIFF are supported; other formats depend on installed Windows codecs. Each source picture must fit the import limits of 20 MiB, 40 megapixels and 16,000 pixels per side. Large PNG conversions may be reduced proportionally in the imported copy.

## Your files

All active usage data stays beside the executable in **Log**:

| Location | Contents |
| --- | --- |
| `Log/Data` | Both feeds, review history, separate drafts, recovery saves, images and the optional profile |
| `Log/Backups` | Requested backups and a safety snapshot before Restore |
| `Log/window.json` | Window width, saved after an adjustment |
| `Log/Diagnostics` | Bounded error logs; these may contain affected file paths |

Keep the entire **Log** folder when moving or updating ClearNote. Backups include both feeds, their valid drafts and images. Restore validates an archive before replacing the library and preserves the previous data folder.

Deleting a post clears its active and recovery records. A locally copied picture is removed when no other post, profile or recoverable draft uses it. Original source files and manually exported archives remain independent. Closing the app or deleting a post does not create an automatic archive.

## Build

Use Windows, PowerShell and the .NET 10 SDK:

```powershell
./src/build.ps1
```

This builds the solution, runs Core and WPF component checks with synthetic data, and publishes the portable executable to `app`. The app has no external package dependencies. The Windows workflow performs the same build and checks and prepares a portable artifact.

The maintained projects are `Folio.Core`, `Folio.App`, `Folio.Tests` and `Folio.UiTests`. The internal Folio names remain for compatibility. App depends on Core; Core has no WPF dependency.

See [architecture](docs/ARCHITECTURE.md), [contributing](CONTRIBUTING.md), and [verification](docs/VERIFICATION.md) for development details.

## License

[MIT](LICENSE).
