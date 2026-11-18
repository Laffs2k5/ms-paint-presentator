# CLAUDE.md

Working agreement for this repo. Where it differs from your user-level defaults,
this file wins.

## Git workflow

- **Commit directly on `main` and push to `origin`.** No feature branches and no
  draft PRs for now.
- Atomic commits with conventional-commit subjects, and a body that explains
  *why*. Each commit should be reviewable and revertable on its own.
- Keep the history clean: the repo may be made public. Don't push WIP, fixup or
  "oops" commits. Fix a local commit before pushing rather than after.
- Once CI exists, PRs are fine, **as a CI gate only**: no review approval is
  required. Merge when the checks pass, with rebase merge. Never squash, never a
  merge commit.

## Public-ready content

Treat everything you commit as if it is already public.

- **No copyrighted third-party material.** No verbatim transcripts, copied
  articles, copied docs, or copied code. Paraphrase, link to the source, and keep
  any direct quote short and attributed.
- **No license violations.** The repo is MIT (see [LICENSE](LICENSE)). Only take
  in code and dependencies whose licenses are compatible with that, and keep any
  attribution the license requires.
- No secrets, credentials, or personal or employer-internal details.
