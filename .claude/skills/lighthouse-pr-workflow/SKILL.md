---
name: lighthouse-pr-workflow
description: Use when opening PRs or hearing "PR dédiées" in lighthouse.
category: workflow
---

# Lighthouse PRs: every branch off `main`, parallel only when independent

"PR dédiées" means one PR per change. Every PR branch starts from an up-to-date `origin/main`, never from another PR branch.

1. Independent PRs (the second one does not need code from the first) may be open at the same time. Branch each one from `main`.
2. Dependent PRs (the second one uses code from the first): wait for the first one to merge, then branch from the updated `main`. Never stack a branch on another PR branch unless the user has approved it for that specific case. You may ask for that approval, but you must not do it on your own.
3. Label every PR with exactly one of `release-major`, `release-minor`, `release-patch` (see RELEASING.md).
4. Avoid rebases. If one is unavoidable, tell the user before running it.
