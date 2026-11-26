# Releasing

Releases are **fully automated** from pull request labels. There is no manual
version bump, tag, or changelog edit — CI does all of it on merge to `main`.

## How it works

1. Open a PR against `main`.
2. Apply **exactly one** release label:
   - `release-major` — breaking changes (`X.0.0`)
   - `release-minor` — new features (`x.Y.0`)
   - `release-patch` — bug fixes (`x.y.Z`)
3. Merge the PR.
4. On merge, the centralized release workflow
   (`.github/workflows/release.yml`, which calls
   [`JordanOlivet/ci-workflows`](https://github.com/JordanOlivet/ci-workflows))
   automatically:
   - bumps the [`VERSION`](VERSION) file according to the label,
   - regenerates [`CHANGELOG.md`](CHANGELOG.md),
   - builds and tests the backend (.NET 10) and frontend (Node 24),
   - builds and pushes the Docker image to GHCR
     (`ghcr.io/jordanolivet/lighthouse`),
   - creates the GitHub release and tag.

A PR merged **without** a release label does not trigger a release, and neither does a PR
closed without merging, whatever its labels.

## Version source of truth

The current version lives in the [`VERSION`](VERSION) file at the repo root.
All build args, the API `/api/system/version` endpoint, and the Docker image tag
derive from it. Do not edit `VERSION` by hand — the release workflow owns it.

## Branch CI (no release)

PRs also run [`pr-ci.yml`](.github/workflows/pr-ci.yml): backend build + tests,
frontend build + type-check, and a Docker image build pushed under a
branch-scoped tag (`<version>-<branch>` and `latest-dev`). This validates the
change without cutting a release.

## Pre-merge checklist

- [ ] `build-check` passes locally (backend build + frontend check).
- [ ] `dotnet test` passes.
- [ ] Exactly one `release-*` label applied (or none, if no release is intended).
- [ ] PR description explains the change and how it was tested.

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full dev workflow.
