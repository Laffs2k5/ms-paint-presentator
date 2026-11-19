# CLAUDE.md

Working agreement for this repo. Where it differs from your user-level defaults,
this file wins.

## Git workflow

- **Work lands through pull requests**, one coherent change per branch and PR.
  CI (`.github/workflows/ci.yml`: build and every test on `windows-latest`) is
  the gate: no review approval is required. Merge when the checks pass, with
  **rebase merge**. Never squash, never a merge commit. Nothing is pushed to
  `main` directly.
- Atomic commits with conventional-commit subjects, and a body that explains
  *why*. Each commit should be reviewable and revertable on its own.
- Keep the history clean: the repo is public (since 2026-11-19), and so are
  its pull requests, their descriptions and the release notes. Don't push WIP,
  fixup or "oops" commits. Fix a local commit before pushing rather than after.
- **Paint is not tested in CI** (a runner has no desktop to drive it on). A
  change to the presenter's Paint side is also run by hand: `presenter doctor`
  and `presenter present <deck> --verify`, which drive the real screen.

## Public content

Everything you commit, and everything you write on GitHub here, is public.

- **No copyrighted third-party material.** No verbatim transcripts, copied
  articles, copied docs, or copied code. Paraphrase, link to the source, and keep
  any direct quote short and attributed.
- **No license violations.** The repo is MIT (see [LICENSE](LICENSE)). Only take
  in code and dependencies whose licenses are compatible with that, and keep any
  attribution the license requires.
- No secrets, credentials, or personal or employer-internal details.
