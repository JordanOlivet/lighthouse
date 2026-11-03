# Generic webhook notifications — design

Issue: [#237 Webhook notification](https://github.com/JordanOlivet/lighthouse/issues/237)
Date: 2026-11-03

## Intent

Lighthouse can already send automatic-update notifications to a Discord webhook.
Users who don't use Discord (the requester runs **Apprise**) have no way to receive
them. Add a second, **generic HTTP webhook** channel that sends the same events as
JSON to any URL, so it works with Apprise out of the box and with anything else
(n8n, Home Assistant, ntfy-style relays, custom scripts) via structured data.

## Goals

- A generic webhook channel, configured in Settings → Notifications, **independent**
  of Discord (either, both, or neither can be enabled).
- The same events Discord gets today, plus a test event:
  - `app.update.started`, `app.update.failed`, `app.update.completed` (Lighthouse self-update)
  - `compose.autoupdate` (which projects/services were updated, with image + digest change)
  - `images.prune` (removed images, space reclaimed)
  - `test` (the "Test" button)
- A fixed JSON payload that Apprise's `/notify` endpoint accepts as-is
  (`title`, `body`, `type`) **and** that carries structured data (`event`, `data`)
  for other consumers.
- Optional **free-form request headers** for auth or anything else a receiver needs
  (the requester's Apprise needs none, but other setups might).
- Best-effort delivery, like Discord: a failure is logged, never breaks an update flow.

Reference setup (requester): Apprise API on the same docker network as Lighthouse,
URL `http://apprise:8000/notify/homelab` (stateful `/notify/{key}` form, no tags,
no auth). This must work with zero extra configuration.

## Non-goals

- Apprise stateless `/notify` (needs `urls` in the body) and tag filtering (`tag`
  field). Possible follow-up: an optional extra field merged into the payload.

- Multiple generic webhooks (Apprise already fans out; can be added later).
- User-defined payload templates.
- Per-event filtering (enable/disable individual events).
- Retries / delivery queue.
- Changing Discord behaviour or its payload.

## Anti-goals

- Leaking secrets: header values must never be returned by the API or written to logs.
- A webhook failure (timeout, 500, DNS) delaying or failing an update cycle, or
  preventing Discord from being notified (and vice versa).

## Payload

`POST <url>` with `Content-Type: application/json`, plus the user's headers.

Top-level fields (always present):

| Field | Type | Notes |
|---|---|---|
| `title` | string | Same wording as the Discord embed title (emoji included). |
| `body` | string | Plain text (no Markdown), multi-line. Human summary of the event. |
| `type` | string | Apprise notification type: `info` \| `success` \| `warning` \| `failure`. |
| `event` | string | One of the event names above. |
| `timestamp` | string | ISO-8601 UTC. |
| `data` | object | Event-specific structured data (below). |

`type` mapping:

| Event | type |
|---|---|
| `app.update.started` | `info` |
| `app.update.failed` | `failure` |
| `app.update.completed` | `success` |
| `compose.autoupdate` | all ok → `success`; some failed and some ok → `warning`; all failed → `failure` |
| `images.prune` | `success` |
| `test` | `info` |

`data` per event (camelCase JSON):

- `app.update.started`: `{ "fromVersion", "toVersion" }`
- `app.update.failed`: `{ "fromVersion", "toVersion", "error" }`
- `app.update.completed`: `{ "version" }`
- `compose.autoupdate`:
  ```json
  {
    "updatedCount": 2,
    "failedCount": 1,
    "projects": [
      {
        "name": "coder",
        "success": true,
        "error": null,
        "services": [
          { "name": "coder", "image": "ghcr.io/coder/coder:latest",
            "oldDigest": "a1b2c3d4e5f6", "newDigest": "0f9e8d7c6b5a" }
        ]
      }
    ]
  }
  ```
  Maps 1:1 from `ComposeAutoUpdateReport` / `ProjectUpdateResult` / `ServiceChange`
  (`oldDigest`/`newDigest` are the existing `OldDigestShort`/`NewDigestShort`).
  **All** projects are included (no 25-field cap as with Discord).
- `images.prune`: `{ "images": [ "...", ... ], "count", "spaceReclaimedBytes", "danglingOnly" }`
  (`images` = the raw references as received; full list, no truncation).
- `test`: `{}`

Example `body` for `compose.autoupdate`:

```
✅ coder
  • coder (ghcr.io/coder/coder:latest): a1b2c3d4e5f6 → 0f9e8d7c6b5a
❌ authelia
  ⚠ pull failed: ...
```

Same events as Discord are skipped as today: no notification for an empty compose
report or an empty prune list.

## Configuration

Three new `AppSettings` keys, read on each send (no restart needed), same pattern
as Discord:

| Key | Value |
|---|---|
| `NotificationsWebhookEnabled` | `true` / `false` |
| `NotificationsWebhookUrl` | absolute `http` or `https` URL (any host; `http` allowed because receivers like Apprise usually live on the LAN / docker network) |
| `NotificationsWebhookHeaders` | free text, one header per line, `Name: Value` |

Headers rules:
- Blank lines ignored. Each other line must contain `:`; name = text before the
  first `:` (trimmed) and must be a valid HTTP header token; value = the rest, trimmed
  (may contain `:`).
- Rejected names (case-insensitive): `Content-Type`, `Content-Length`, `Host`
  (they'd conflict with what the client sets). *(builder's call — see below)*
- Applied verbatim to the request; no interpretation of auth schemes.

Validation happens in `ConfigController.ValidateAutoUpdateSetting` (same place as
Discord) when the setting is saved, and in the test endpoint. An invalid value is
rejected with a message naming the bad line.

Secrets handling:
- `GET /api/config/settings` returns `NotificationsWebhookHeaders` masked: the
  literal `••••••••` when non-empty, empty string otherwise. The URL is returned
  in clear (decided: admin-only page).
- If `PUT /api/config/settings/{key}` echoes the saved value back, it is masked the
  same way for the headers key.
- `PUT /api/config/settings/{key}` currently logs `"{Key} = {Value}"`. For the
  headers key the value must not be logged (log the key only). *(Discord's URL is
  logged today too; fixing that is out of scope unless trivial — mention it, don't
  silently change it.)*

## Backend

Decided shape:

- `WebhookNotificationService` — new class in `Services/Notifications/`, sends the
  payload above. Mirrors `DiscordNotificationService` (reads settings per call,
  no-ops when disabled or URL empty, swallows + logs failures). Typed `HttpClient`
  with a **10 s timeout** so a dead receiver can't stall an update cycle.
- Fan-out: callers keep depending on `INotificationService`. It resolves to a new
  composite that calls Discord and Webhook **concurrently** (`Task.WhenAll`, each
  call wrapped individually); each channel is isolated (one throwing or hanging
  can't skip or delay the other). Concurrency matters for `app.update.started`:
  the container may be recreated shortly after, so a slow Discord call (100 s
  default timeout) must not delay the webhook send. The 4 existing callers
  (`AutoUpdateAppBackgroundService`, `AppUpdateNotificationStartupService`,
  `AutoUpdateComposeBackgroundService`, `AutoPruneImagesBackgroundService`) don't change.
- Test endpoints are per channel:
  - existing `POST /api/notifications/test` keeps testing Discord (unchanged contract);
  - new `POST /api/notifications/test/webhook`, body `{ "url"?: string, "headers"?: string }`.
    `url` null → saved URL; `headers` null → saved headers (lets the user test an
    unsaved URL without retyping hidden headers); `headers` empty string → no headers. Works even when the channel is disabled
    (same as Discord). Returns the receiver's status + truncated body on failure.
- Shared formatting helpers currently private in `DiscordNotificationService`
  (`FormatBytes`, `FormatImageRef`, `Truncate`) may be moved to a small internal
  static helper so both channels use them. Discord's output must stay byte-identical.
- Header parsing/validation lives in one place (e.g. a `WebhookHeaders` static
  helper analogous to `DiscordWebhookUrl`) used by the service, the config
  validation and the test endpoint.

## Frontend

- `stores/notifications.svelte.ts`: add the 3 keys + state, a `testWebhook(url?, headers?)`.
- `api/notifications.ts`: `testWebhook` → `POST /notifications/test/webhook`.
- `routes/(protected)/settings/+page.svelte`, Notifications tab: a second card
  **"Webhook"** under the Discord card, same look and behaviour:
  - enable toggle (saved on change);
  - URL input (saved on blur/enter when edited, shown in clear);
  - headers textarea, monospace, placeholder `Authorization: Bearer <token>`; when
    configured it shows the masked value and is only saved if the user edits it
    (same "dirty" logic as the Discord URL). Saving an empty textarea clears headers;
  - help text mentioning the payload is Apprise-compatible (`title`/`body`/`type`)
    and includes `event`/`data` for other tools;
  - "Test" button: sends the typed URL/headers if edited, otherwise saved values.
- i18n: new keys in `en.ts`, `fr.ts`, `es.ts` under a prefix that doesn't clash with
  the existing Discord keys `settings.notifications.webhookUrl/webhookPlaceholder/webhookHelp`
  (e.g. `settings.notifications.generic*`). Existing Discord keys are not renamed.

## Testing

Backend (xUnit + Moq + FluentAssertions, mirror `DiscordNotificationServiceTests`
with a fake `HttpMessageHandler`):
- payload per event: `title`/`body`/`type`/`event`/`data` shape and `type` mapping;
- headers are sent; disabled / empty URL → no request; receiver 500 / timeout → no throw;
- test endpoint uses override URL/headers, falls back to saved ones;
- header parser: valid lines, value with `:`, blank lines, missing `:`, bad name, forbidden name;
- composite: one channel throwing doesn't prevent the other from being called;
- settings: headers masked in `GET`, invalid URL/headers rejected on `PUT`.

Frontend: `npm run check` passes; manual check against a real Apprise container
and a request-bin style receiver.

## Left to the builder

- Exact class names (`CompositeNotificationService`, `WebhookHeaders`, ...).
- Whether `SendTestAsync` stays on `INotificationService` or moves to the concrete
  channel classes (the composite has no sensible single "test").
- Exact UI copy and translations.
- Whether to move the shared formatting helpers or duplicate the two tiny ones.

## Calls made by Claude (not explicitly discussed)

- `http` URLs allowed (LAN receivers).
- Body is plain text, not Markdown; no Apprise `format` field sent.
- 10 s HTTP timeout.
- `Content-Type` / `Content-Length` / `Host` rejected as custom headers.
- Full project/image lists in `data` (no truncation), `body` uses the same
  per-project lines as Discord.
- Header values masked as a whole (`••••••••`), not per line.
- Discord and webhook sent concurrently, not in sequence.
- `null` values are serialized in `data` (stable shape for consumers), unlike
  Discord's serializer which omits them.
- Redirect handling: builder's call (default `HttpClient` behaviour is acceptable).
