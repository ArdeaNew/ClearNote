# Contributing to ClearNote

ClearNote is a small, offline Windows notebook. Contributions should keep its quiet interface, portable data ownership and straightforward dependency structure intact.

## Development setup

Use Windows x64, PowerShell and the .NET 10 SDK with its desktop targeting support. The published executable includes the runtime. The projects use only the .NET and WPF libraries supplied by Microsoft; there are no NuGet package dependencies to install manually.

From a clean source checkout, run:

```powershell
dotnet build src/Folio.slnx -c Release -m:1 -p:UseSharedCompilation=false
dotnet run --project src/Folio.Tests/Folio.Tests.csproj -c Release --no-build -- ./checks
dotnet run --project src/Folio.Tests/Folio.UiTests/Folio.UiTests.csproj -c Release --no-build -- ./checks
```

The check projects are executable regression harnesses, so `dotnet test` does not run them. Each invocation creates a unique subfolder containing synthetic data only. A failing check returns a nonzero exit code. Core checks run independently of WPF; the UI checks require Windows and an STA thread, which their entry point supplies.

To build, check and publish in one step, use `./src/build.ps1`. Its output goes into the ignored `app` directory. For a separate publish directory, use:

```powershell
dotnet publish src/Folio.App/Folio.App.csproj -c Release -p:PublishProfile=Portable -m:1 -p:UseSharedCompilation=false -o ./app
```

Do development runs against a disposable library:

```powershell
./app/ClearNote.exe --data ./work/development/Log/Data
```

The path after `--data` is the library folder itself. This flag avoids opening a portable installation's normal library. Do not point regression checks at your own notebook. The application creates first-run welcome posts when that development library is new.

## Code boundaries

Read [the architecture guide](docs/ARCHITECTURE.md) before changing storage or cross-feed behavior. Keep pure data, validation and scheduling in `Folio.Core`. Keep WPF, clipboard integration, dialogs, image decoding and window preferences in `Folio.App`. Do not introduce a dependency from Core to App or a service locator for a local operation.

Prefer small changes with a concrete user-visible reason. Reuse the existing editor, feed and attachment controls across all four collections. Keep the internal Folio assembly and namespace names unless a separate, tested compatibility migration requires a change.

## Data changes

Saving and restoring data must preserve the existing atomic-write, conflict-detection and validation guarantees. A failed operation must leave recoverable files intact. Treat libraries, drafts, profile images, recovery snapshots and imported pictures as separate ownership concerns. A picture can be shared across collections, the profile and any current or recoverable draft.

When changing a saved model, provide a compatible default for older libraries where possible and add a synthetic old-schema fixture. Validate the entire candidate before replacing a healthy library. Never add a migration that deletes its source as part of an ordinary first launch.

When changing the image path, verify that imported files remain independent of their sources, stored references remain bounded to the asset folder, and deletion retains pictures with another owner. Optional Windows image codecs must fail with a useful message when unavailable.

## Interface changes

Use English interface text, a white base, no gradients and the established monospace/FangSong typography. Preserve keyboard editing, text selection and ordinary vertical feed scrolling. Width controls should present plain actions rather than numeric proportions. Review scheduling and browse recording apply only to Notes.

For a behavior change, run the relevant checks and the full two-harness regression before proposing a merge. Add a check for a meaningful failure or compatibility case, rather than duplicating implementation details. Use offscreen control rendering to inspect layout where useful, and report it as offscreen verification. Native title-bar behavior, monitor transitions and desktop pointer interaction still need a separate manual check.

README screenshots live in `docs/images`. Keep them focused on the current interface, use synthetic content, and regenerate them when a visible change makes them misleading. Avoid embedding user notes or machine-specific paths in screenshots.

To render the continuous-feed showcase, put three photographs you have permission to publish in a separate folder. After building, run:

```powershell
dotnet run --project src/Folio.Tests/Folio.UiTests/Folio.UiTests.csproj -c Release --no-build -- ./checks --render-showcase --photos-dir C:/Pictures/Showcase --output ./work/showcase
python src/tools/Encode-Showcase.py ./work/showcase ./work/showcase/feed-scroll.gif
```

The optional GIF encoder requires Pillow. It is a documentation tool, not an application dependency. Review the long feed, animation, Memo, Review, Opinions and Analysis images, then copy only those six exports into `docs/images/showcase`. The renderer uses actual application controls and an isolated example library. Do not commit source photographs, frame sequences, manifests or example libraries; exported images contain no source-file metadata.

## Pull requests and issue reports

Describe the problem, the resulting behavior and the checks performed. Include a small synthetic reproduction and a screenshot for a visual problem. Never attach a real `Log` folder, a personal backup, imported private pictures, access tokens or unredacted diagnostic paths. Diagnostic entries can include local file paths even though they do not serialize note bodies.

Review the staged file list before pushing. The repository excludes portable binaries, build output, notebook data, backups and local credentials. An ignore rule is a guardrail, so also inspect any intentionally added asset or fixture. Do not force-add ignored user data.

## CI and distributables

The Windows workflow builds the solution, runs both synthetic harnesses and publishes a portable executable. It uploads only an explicitly listed executable, README and license as a temporary Actions artifact. It does not create a GitHub Release, modify a user's installation or upload test libraries.

The source, including its application-owned welcome landscape, follows the repository license. Use original assets or assets with a compatible license and document their origin when adding new artwork.
