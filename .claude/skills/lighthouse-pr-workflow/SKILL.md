---
name: lighthouse-pr-workflow
description: Use when opening PRs or hearing "PR dédiées" in lighthouse.
category: workflow
---

# Lighthouse PRs: one at a time, never stacked or parallel

Open one PR, wait for it to merge, then branch from the updated `main` for the next one. "PR dédiées" means one PR per change, sequenced, not several open at once.

1. Before opening a second PR, check that the first one is merged. If not, say which PR blocks it and wait.
2. Label every PR with exactly one of `release-major`, `release-minor`, `release-patch` (see RELEASING.md).
3. Avoid rebases. If one is unavoidable, state it to the user before running it.
