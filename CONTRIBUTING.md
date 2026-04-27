# Contributing to WKVRCProxy

Thanks for your interest. This file is the short version — the README has the full architecture and runtime overview, and `GEMINI.md` has the engineering standards. Skim both before a non-trivial change.

## Before you start

- The project is alpha. Behaviour changes faster than docs do; trust the code over old commentary.
- This is a single-maintainer repo. For anything beyond a small fix, **open an issue first** so we can agree on direction before you spend time.
- The codebase deliberately avoids heavy abstraction. A new feature is usually a method on an existing service, not a new service.

## Setting up

You need: Windows 10/11 x64, .NET 10 SDK, Node.js 20+, PowerShell 5.1+, Git. See the README's "Requirements" and "Building" sections.

```powershell
git clone https://github.com/RealWhyKnot/WKVRCProxy.git
cd WKVRCProxy
powershell -ExecutionPolicy Bypass -File build.ps1
```

The first `build.ps1` run also configures `git config core.hooksPath = .githooks`, which activates the commit-msg hook (see below).

## Branch & PR flow

1. Fork, branch off `main`. Branch name is up to you (`fix/...`, `feat/...`, `chore/...` are common).
2. Make your change. Keep diffs small and focused — a "while I'm here" cleanup is its own PR.
3. `dotnet test src/WKVRCProxy.HlsTests` must pass. Add a test for behavioural changes.
4. Run `powershell -File build.ps1` once on the branch before opening the PR — it catches vendor / publish issues that `dotnet test` won't.
5. Open the PR; the template tells you what to fill in.

## Commit messages

Subject style follows the existing log: `type: short summary (YYYY.M.D.N-HASH)`.

- `type` is one of `feat`, `fix`, `build`, `docs`, `refactor`, `test`, `chore`.
- The `(YYYY.M.D.N-HASH)` build-version stamp at the end is automatic — `build.ps1` produces it. **Do not paste the same stamp twice in one subject.** The `.githooks/commit-msg` hook will reject duplicates (it's a footgun caused by editor-template autocompletion).
- Body explains the *why*, not the *what* — the diff is the what.

## Code style

- C# follows the surrounding file. There is no `.editorconfig` of record yet.
- **Do not use C# string interpolation (`$"..."`) in files that `build.ps1` rewrites with regex.** It tangles with the version-stamp regex. `GEMINI.md` lists the affected files and the safe alternatives.
- Vue/TypeScript: keep the existing per-component pattern. Pinia store changes go through `appStore.ts`.
- For non-obvious decisions, leave a short comment on *why*. The `=== WHY WE WRAP ===` block in `ResolutionEngine.cs` is the model.

## What to avoid in PRs

- Refactors bundled with feature work — split them.
- Introducing a new service/abstraction for a one-off case.
- Mocking the database, mocking the relay, or mocking AVPro behaviour. Tests should hit the real components where practical (the xUnit suite is the source of truth here).
- Pulling in third-party binaries by hand. All vendored tools live in `vendor/` and are managed by `build.ps1` with pinned versions.

## Reporting bugs / asking questions

- **Bug**: use the [bug report template](https://github.com/RealWhyKnot/WKVRCProxy/issues/new?template=bug_report.yml). Include the correlation-ID block from the Logs view — it's the difference between "we'll figure it out" and "this is unfixable from here".
- **Question / setup help**: open a [Discussion](https://github.com/RealWhyKnot/WKVRCProxy/discussions). We'll convert to an issue if it turns out to be a bug.
- **Security issue**: see [SECURITY.md](.github/SECURITY.md) — use the private GitHub advisory flow, never a public issue.
