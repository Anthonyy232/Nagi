# Working on Nagi

A local-first music player for Windows: C#, .NET 10, WinUI 3, packaged as MSIX. Run the `Nagi (Package)` profile of `Nagi.WinUI` to see it, after reading Testing below.

## Code

- Simple and direct. No speculative abstractions, options or extension points.
- Stay in scope; suggest extras instead of adding them.
- Use what the platform, framework, dependencies and this repo already provide before writing a helper.
- Validate only at real boundaries (user input, storage, network, other processes). No checks, fallbacks or try/catch for cases that can't happen.
- One source of truth for each value or rule.
- Delete dead code you find or create.
- Match the surrounding code.
- Fix root causes, and fix the same pattern everywhere it occurs.

## Comments

- Only where the reason isn't clear from the code: a quirk, a race, a non-obvious decision.
- Short fragments, not sentences. Never restate code, narrate, or label sections.
- Delete comments that no longer match.

## Tests

- Behaviour and the edges that can break. No trivial, duplicate, obvious or low-signal tests.
- A regression test fails without its fix.
- Deterministic: fake time and observable events, no sleeps.
- A failing test is a bug until shown otherwise.

## Layers

- `Nagi.Core` and `Nagi.Presentation` are platform-neutral: no Windows types, and logic another UI head would need goes there, not in `Nagi.WinUI`.
- View models expose neutral values (`AppIcon`); the WinUI head maps them to its own assets (`AppIconGlyphs`).
- View models own their errors, so pages bind and call without try/catch. `IUIService` never throws: a dialog it can't show returns what a cancel would.
- Sibling pages share base classes and helpers (`SongListPage`, `CollectionPage`, `SongListToolbar`); change one, check the others.

## UI and strings

- Theme resources and the shared styles in `Styles/`, never hard-coded colours, sizes or radii.
- Every icon-only button gets an `AutomationProperties.Name`, normally its tooltip string.
- See UI changes in light and dark and on each backdrop (Mica, Mica Alt, Acrylic).
- Every UI string is localised. Add it in English only to `src/Nagi.Presentation/Resources/Strings.resx` (Core messages: `src/Nagi.Core/Resources/Strings.resx`); Crowdin translates and opens its own PR from `crowdin-translations`. Leave that branch and the locale files to it.
- Removing a key: remove it from every locale file. Renaming a key or moving a `.resx` loses its translations.
- Whole strings with ICU plurals (`ResourceFormatter`), never sentences built from fragments.

## Dependencies and data

- Package versions live in `Directory.Packages.props` (central package management), not in project files.
- An EF Core model change needs a migration: `dotnet ef migrations add <Name> --project src\Nagi.Core --startup-project src\Nagi.Core --context MusicDbContext --no-build`.
- Anything new sent online goes in `PRIVACY.md`.

## Testing

- A dev build registers as package `48743Nagi.Nagi`, the same identity as the owner's Store install, and would share its data. Never deploy it over that install, and never touch `%LOCALAPPDATA%\Nagi` or the owner's music library: use a different package identity (uncommitted) and an invented library, and remove both afterwards.
- Loudness tests need `ffmpeg` on `PATH`.

## Done means

- Diff reviewed against this file.
- `dotnet build Nagi.sln -c Debug -p:Platform=x64` then `dotnet test --solution Nagi.sln -p:Platform=x64 --no-build` pass with zero warnings.
- UI changes seen in the running app, light and dark. Docs and metadata changes need no end-to-end run.
- README updated for anything users will notice; behaviour changes called out.

## Docs

- README is for users: what it does, how to get it, how to use it. No internals, no agent instructions.
- This file is for agents: only what can't be found quickly in the code.
- No meta docs in the repo (notes, audits, plans, reports).

## Git

- `type: summary` or `type(scope): summary`: lowercase, imperative, no full stop, about 70 characters at most. Types: `feat` `fix` `refactor` `perf` `test` `docs` `style` `build` `ci` `chore`.
- Body only to say why. `Fixes #123` closes an issue.
- One logical change per commit. Squash fix-ups before merging.
- Branch off `master`. A single commit fast-forwards; several merge with `merge: <summary>`.
- No AI attribution in commits, branches, PRs or releases.
- One maintainer: amending and force-pushing to tidy history is fine. Keep contributors as authors. Delete stale branches.
- Old history stays as it is: forks, the Crowdin branch and its PR pin those commits.

## Pull requests

- Title in commit format. Body: what changed, why, how it was checked. Short.

## Releases

- Bump commit `chore: release X.Y.Z`: the version in `Directory.Build.props` and `Package.appxmanifest`. Tag `X.Y.Z`, no `v`; the tag push builds the MSIX.
- Then run the `Create Release` workflow with the tag: SignPath signs the bundle after the owner approves, and a draft release gets `Nagi.msixbundle` and `Nagi.appinstaller`. Installed copies update from `releases/latest/download/Nagi.appinstaller`, so never rename those assets or a tag.
- Title `Nagi X.Y.Z`.
- Notes: banner image first, then `### New`, `### Improved`, `### Fixed` (only those that apply). One line per change, written for users; no internals, dependency lists or test counts. End with the install link(s) and the full changelog link.
- Banner: the art at the top of the previous release, reused. Banners made for releases without it live on the orphan `media` branch as `releases/<tag>.png`, beside the script that draws them.
- The `romanization-packs` release is the feed the app downloads lyrics packs from: keep its tag and asset names.
- The Microsoft Store submission is the owner's, by hand.

## Agents

- Pushing, amending and force-pushing are allowed. Ask before publishing a release or submitting to a store.
- Leave changes you didn't make alone. Clean up scratch files, processes and emulators.
- Say what was checked and how, and what wasn't.
- Short replies; one recommendation, not a list of options.
