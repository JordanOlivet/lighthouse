---
name: lighthouse-image-update-checks
description: Use when image update checks fail on ${VAR} or 429.
category: workflow
---

# Interpolate compose variables before any registry call

Compose image values can contain `${VAR:-default}`. Resolve them before calling the registry client. Passing the raw string throws `UriFormatException`, and the project is silently never checked.

Resolution order, matching docker compose: process environment, then project `.env`, then the global env file from AppSettings. An unset variable with no default skips that one image with a warning. It must not fail the whole project.

- Read env files only when the image contains `$`.
- Helper: `ComposeVariableInterpolator` in `Utils/`. Env file list: `IComposeEnvFileResolver.GetEnvFilePathsAsync`.
- GHCR 429 `retry-after` is in microseconds (logs show it as `�s`), not seconds. Throttles usually clear in 1 to 4 minutes, longer than the in-pull retry window, so deferring the retry to the end of the cycle is the fix to try first.
