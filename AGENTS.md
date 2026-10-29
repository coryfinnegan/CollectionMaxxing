# CollectionMaxxing: agent guide (short form)

This repo's full agent instructions live in [CLAUDE.md](CLAUDE.md) — hard
rules, workflow, branches, and definition of done. That file is authoritative;
what follows is only the map.

- **What this is:** [PRODUCT.md](PRODUCT.md) — a personal collection tracker
  (.NET API plus an Expo app for iPhone and web).
- **How it looks and reads:** [DESIGN.md](DESIGN.md) — the Phase 0 design
  system. DESIGN.md outranks the generic agent skills on any UI question.
  New UI must pass the app gate's design ratchet (no raw colors, no inline
  styles, no raw text, no doubled titles) and the visual-regression spec.
- **Merging:** agents build and ship everything here (a "software factory"
  experiment); the owner never reviews or merges. Once CI is green and an
  independent verifier has approved a PR, squash-merge it without asking.
  The details are in CLAUDE.md, "Merging".
- **Skills:** `.agents/skills/` (and the mirrored `.claude/skills/`) carry the
  Expo skills, `frontend-design`, and the repo's own `collection-design`.
  The two skill trees must stay identical.
