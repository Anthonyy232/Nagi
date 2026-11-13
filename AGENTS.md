# Working on Nagi

Guidance for anyone changing this codebase, human or AI. These are defaults, not laws: follow them unless you have a good reason not to, and say what the reason was.

Nagi is a local-first music player for Windows built with C#, .NET 10 and WinUI 3. See the [README](README.md) for features.

## Architecture

The solution is layered so that everything except the Windows shell is platform-neutral and could back another UI (for example a macOS or Linux head).

| Project | Owns | May reference |
| --- | --- | --- |
| `Nagi.Core` | Library, database (EF Core + SQLite), playback queue, playlists, metadata, lyrics, scrobbling, music servers, file system access | Nothing UI |
| `Nagi.Presentation` | View models, app services (settings, navigation), the abstractions a UI head implements, localized UI strings | `Nagi.Core` |
| `Nagi.Audio.LibVlc` | The LibVLC audio player behind `IAudioPlayer` | `Nagi.Core` |
| `Nagi.WinUI` | XAML pages, windows, tray, taskbar, system media controls, Win32 interop, DI composition, MSIX | Everything above |

- **Code is grouped by feature, not by kind.** Each project's folders are features (`Playback`, `Library`, `Lists`, `Settings`), namespaces follow the folders, and the test projects mirror them.
- **Put logic in the lowest layer that can own it.** If another head would need it too, it belongs in Core or Presentation, not in a WinUI page or service. WinUI code should be about Windows: XAML, windowing, interop.
- **No Windows types in Core or Presentation.** Small, guarded OS specifics are fine in shared code (`OperatingSystem.IsWindows()`).
- **View models don't choose platform assets.** They expose neutral values such as `AppIcon`, and each UI head maps them to its own icons (WinUI: `AppIconGlyphs`).
- **Services are interfaces registered in DI**, one registration entry point per layer (`AddNagiCore`, `AddNagiPresentation`, `AddNagiApp` for WinUI). Code that a test or another platform needs to replace goes behind an interface; other code doesn't need one.
- **MVVM with the CommunityToolkit** (`[ObservableProperty]`, `[RelayCommand]`). Pages hold view-only code; workflows and decisions live in view models.
- **View models own their errors.** Commands and methods that views call log and handle their own failures, so pages bind or call them without try/catch. `IUIService` never throws: a dialog or picker the platform can't show returns what a cancel would.
- **Pages share behaviour through base classes and helpers** (`ViewModelPage`, `SongListPage`, `CollectionPage`, `SongListToolbar`, `SearchToggle`, `EmptyState`). Similar pages should look and behave the same; when you change one, check its siblings.
- **Prefer what the platform gives you.** Built-in control behaviour (for example list selection and Ctrl+A), toolkit features and existing helpers come before custom code.

## How we write code

- **Simple beats clever.** Write the most direct code that solves today's problem. No speculative abstractions, options or extension points for needs that don't exist yet.
- **Stay in scope.** Build what was asked; suggest extras instead of adding them.
- **Don't hand-roll what already exists**, in the framework, a dependency, or this repo. Search before you write a helper.
- **Not overly defensive.** Validate at boundaries (user input, files, network, public service entry points) and trust internal callers. Don't wrap everything in try/catch or null checks that can't fail.
- **One source of truth.** Avoid state that duplicates something the framework or another component already tracks.
- **Delete dead code** you come across or create: unused members, commands, strings, resources, usings and packages.
- **Match the surrounding code**: naming, structure, comment density and idioms. Consistency across files matters more than personal preference.
- **Fix root causes.** If a fix needs a workaround, explain why in a comment.
- **Fix it everywhere.** After fixing a bug or a convention slip, look for the same pattern elsewhere.

## Comments

- Explain **why**, not **what**: a constraint, a platform quirk, a non-obvious decision, a race being prevented.
- Don't restate names or code, narrate the next line, or label sections (`#region`, `// ── Section ──` banners).
- XML docs on public APIs only when they add something the signature doesn't: units, ranges, what `null` means, threading or ordering guarantees.
- Remove comments that no longer match the code.

## Tests

- Test **behaviour and the edges that can break**: ordering, cancellation, races, error paths, migrations, parsing.
- Skip trivial, duplicate or obvious tests: constructors, default values, property getters, or "calls the dependency it obviously calls". One meaningful test beats several that restate each other.
- Keep tests deterministic. Prefer fakes, `TimeProvider` and explicit synchronization over sleeps; if timing is unavoidable, keep it short and run the test repeatedly to prove it is stable.
- Loudness tests need `ffmpeg` on `PATH`.

```powershell
dotnet build Nagi.sln -c Debug -p:Platform=x64
dotnet test --solution Nagi.sln -p:Platform=x64 --no-build
```

## Before you call something done

- You have **reviewed your own diff against this file**: over-engineering, over-abstraction, defensive code, hand-rolled or duplicated code, low-signal tests, restating comments.
- It builds with **zero warnings** and **all tests pass**.
- **UI changes are checked in the running app**, in both light and dark themes and with each backdrop (Mica, Mica Alt, Acrylic) where it matters. A green build does not prove the UI works.
- Behaviour changes are deliberate and called out, not side effects of a refactor.
- The README lists new features, and `PRIVACY.md` covers anything new sent online.

## UI conventions

- **Use theme resources** (`ThemeResource` brushes, `ControlCornerRadius`, `OverlayCornerRadius`, the shared styles in `Styles/`) instead of hard-coded colors, sizes or radii.
- **Accessibility:** every icon-only button gets an `AutomationProperties.Name`, normally the same string as its tooltip. Respect reduced motion and high contrast.
- Labels and commands use Title Case. Rarely changed options belong in Settings, not page headers.
- **Strings are always localized.** Add new UI strings to `src/Nagi.Presentation/Resources/Strings.resx` (Core messages go in `src/Nagi.Core/Resources/Strings.resx`) in English only; [Crowdin](https://crowdin.com/project/nagi) handles translations. Reuse an existing string when the meaning is the same, and when you remove a key, remove it from every locale file.
- Don't build sentences from fragments; use whole strings with ICU plurals (`ResourceFormatter`). Renaming a key or moving a `.resx` loses its translations.
- **Logging:** pages log errors and warnings only; navigation is logged centrally. Services log what helps diagnose a bug report, since recent log lines are attached to crash reports.

## Dependencies and data

- Package versions live in `Directory.Packages.props` (central package management). For a major version bump, read the release notes and check every breaking change against how Nagi uses the package.
- Changing the EF Core model needs a migration: `dotnet ef migrations add <Name> --project src\Nagi.Core --startup-project src\Nagi.Core --context MusicDbContext --no-build`.

## Git

- **Commit messages:** `type: summary` in lowercase imperative, describing the outcome, e.g. `fix: keep a maximized window maximized when activating it`. Types: `feat`, `fix`, `refactor`, `perf`, `test`, `docs`, `chore`, `style`, `ci`. Keep messages short; use a brief body (bullets if needed) only to explain why, and add `Fixes #123` when closing an issue.
- **No AI attribution in history:** no `Co-Authored-By` trailers or mentions of AI assistants in commits, branch names or PRs.
- **One logical change per commit.** A refactor, a fix and a feature are separate commits, even when made together. A commit that changes behaviour isn't a `refactor`.
- **Work on a branch off `master`** and merge with a merge commit. Before merging, tidy the branch: squash fix-ups into the commits they fix, and drop commits whose work was later undone.
- Fix bugs that predate your branch on their own branch, and merge that first.
- When reworking a contributor's commits, keep them as the author.
- Don't commit unrelated changes, generated output or secrets. Never force-push `master`.

## For AI agents

- Ask before anything hard to undo or visible to others: pushing (approval to merge isn't approval to push), opening PRs, deleting remote branches, rewriting published history.
- Leave changes you didn't make alone, and clean up your scratch files, branches and processes when done.
- Test the app without touching the developer's own install or library.
- Report results honestly: what was verified and how, what wasn't, and anything you changed on the machine while testing.
- Keep replies and PR descriptions short; use bullet points when they help.
- Prefer one clear recommendation over a list of options; ask only when the decision is genuinely the user's.
