<p align="center"><img src="src/Folio.App/Assets/Folio.svg" width="64" alt="ClearNote alien logo"></p>

# ClearNote

**An offline notebook for Windows, built around a continuous feed.**

[Download for Windows](https://github.com/ArdeaNew/ClearNote/releases/download/v1.9.4/ClearNote-1.9.4-Windows.zip) · [Quick guide](docs/USAGE.md)

<img src="docs/images/showcase/feed-scroll.gif" width="678" alt="Scrolling through ClearNote: short notes, photographs and lists in one continuous feed">

Write at the top. Scroll through your ideas. Double-click a post to edit it where it belongs.

- **Notes** — thoughts, photographs and things worth learning.
- **Memo** — a separate stream for everyday reference.
- **Review** — spaced review of learning notes, filtered by tag.

## Room for the things you notice

A white page, fine dividers and quiet controls. Text and pictures share the same uninterrupted reading column.

<img src="docs/images/showcase/feed-long.png" width="678" alt="A complete ClearNote feed with a composer, short notes, photographs and fine dividers">

Markdown shortcuts, color and highlights keep writing flexible. Add up to four pictures; their originals stay unchanged. Your name and avatar make the notebook yours, without an account.

<details>
<summary>Install, local files and building</summary>

Use **Windows x64** with the **.NET 10 Desktop Runtime**. Extract the download into a writable folder and open `ClearNote.exe`. Current releases are [unsigned](docs/CODE_SIGNING.md).

Your notes, pictures, drafts and preferences stay beside the executable in **Log**. Keep that folder when moving or updating the app. Back up or restore your library from the menu.

To build on Windows with the .NET 10 SDK:

```powershell
./src/build.ps1
```

See [contributing](CONTRIBUTING.md), [architecture](docs/ARCHITECTURE.md) and [verification](docs/VERIFICATION.md).

</details>

[MIT license](LICENSE).
