# Working on Nagi

A local-first music player for Windows: C#, .NET 10, WinUI 3, packaged as MSIX. Published on the Microsoft Store and GitHub; it has real users.

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

## Nagi

- Core and view-model code is platform-neutral: no Windows types there, and logic another UI head would need stays out of the WinUI project.
- Every UI string is localised. Add it in English only; translations come through Crowdin, which opens its own pull request. Leave its branch and the locale files to it, except to remove a deleted string from every locale.
- Whole strings with ICU plurals, never sentences built from fragments.
- Theme resources and shared styles, never hard-coded colours, sizes or radii; every icon-only button has an accessible name. See UI changes on each backdrop as well as in light and dark.
- A dev build has the same package identity as the owner's Store install and would share its data. Never deploy over that install or touch its data or the owner's music library: run under a different, uncommitted identity with an invented library, and remove both afterwards.
- Anything new sent online goes in `PRIVACY.md`.

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
- Old history is not rewritten.

## Pull requests

- Title in commit format. Body: what changed, why, how it was checked. Short.

## Releases

- Bump commit `chore: release vX.Y.Z`; tag `X.Y.Z`, no `v`.
- Title `Nagi X.Y.Z`.
- Notes: banner image first, then `### New`, `### Improved`, `### Fixed` (only those that apply). One line per change, written for users; no internals, dependency lists or test counts. End with the install link(s) and the full changelog link.
- Banner: generated and hosted on the orphan `media` branch; its README has the steps.
- Install links: the Store button, then, from 2.0.0 on, the GitHub button pointing at that release's own `Nagi.msixbundle`.
- The tag push builds the bundle; the `Create Release` workflow has it signed, once the owner approves, and puts it on a draft release. The Microsoft Store submission is the owner's, by hand.
- Installed copies update from the latest release's assets by name, and the app downloads lyrics packs from the `romanization-packs` release: never rename a tag or a release asset.

## Agents

- Pushing, amending and force-pushing are allowed. Ask before publishing a release or submitting to a store.
- Leave changes you didn't make alone. Clean up scratch files, processes and emulators.
- Say what was checked and how, and what wasn't.
- Short replies; one recommendation, not a list of options.
