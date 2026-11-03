# Generic Webhook Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a generic HTTP webhook notification channel (Apprise-compatible JSON) alongside the existing Discord channel, configurable independently in Settings → Notifications.

**Architecture:** A new `WebhookNotificationService` implements `INotificationService` next to `DiscordNotificationService`. A `CompositeNotificationService` becomes the `INotificationService` that callers resolve and fans out to both channels concurrently, isolating failures. Config lives in three new `AppSettings` keys read per call; header values are write-only (masked on read, never logged).

**Tech Stack:** .NET 10 Web API, EF Core (SQLite / InMemory in tests), xUnit + Moq + FluentAssertions; SvelteKit + Svelte 5 runes, Tailwind, i18next.

**Spec:** `docs/superpowers/specs/2026-11-03-webhook-notification-design.md`

## Global Constraints

- Backend commands run from `lighthouse-back/` (solution root); frontend from `lighthouse-front/`.
- Settings keys, verbatim: `NotificationsWebhookEnabled`, `NotificationsWebhookUrl`, `NotificationsWebhookHeaders`.
- Event names, verbatim: `app.update.started`, `app.update.failed`, `app.update.completed`, `compose.autoupdate`, `images.prune`, `test`.
- `type` values, verbatim: `info` | `success` | `warning` | `failure`.
- Payload top-level fields: `title`, `body`, `type`, `event`, `timestamp` (ISO-8601 UTC), `data` — camelCase, `null`s serialized (not omitted).
- URL: absolute `http` or `https`, any host.
- Headers: one `Name: Value` per line; blank lines ignored; split on the **first** `:`; name trimmed and a valid HTTP token; value trimmed; names `Content-Type`, `Content-Length`, `Host` rejected (case-insensitive).
- Masked header value: the literal `••••••••` when non-empty, `""` when empty.
- Webhook HTTP timeout: 10 s.
- Discord payload/behaviour must stay byte-identical; existing `DiscordNotificationServiceTests` must pass unchanged.
- Header values must never be returned by the API or written to logs.
- Every notification send is best-effort: never throws to the caller.
- Lockfile: do not add npm dependencies (none needed).

## Review Focus

1. Headers pasted from a Windows clipboard (`\r\n`) — values must not keep a trailing `\r`. → Task 1 test `TryParse_HandlesCrLf`.
2. Header value containing `:` (e.g. `X-Callback: http://host:1234/x`) — must keep everything after the first colon. → Task 1 test `TryParse_KeepsColonsInValue`.
3. User types a new URL but leaves the (masked) headers untouched, then clicks Test — saved headers must be sent with the new URL. → Task 3 test `SendTest_UsesOverrideUrl_WithSavedHeaders`.
4. Receiver hangs or is down (timeout / connection refused) during an update cycle — no exception escapes, the other channel still sends. → Task 3 test `Notify_DoesNotThrow_WhenRequestTimesOut`, Task 4 test `Notify_CallsAllChannels_WhenOneThrows`.
5. Saving headers then reading settings back — neither `GET /api/config/settings` nor the `PUT` response contains the raw header value. → Task 5 tests `GetSettings_MasksWebhookHeaders`, `UpdateSetting_DoesNotEchoWebhookHeaders`.

---

### Task 1: URL and header validation helpers

**Files:**
- Create: `lighthouse-back/lighthouse-back/src/Services/Notifications/WebhookUrl.cs`
- Create: `lighthouse-back/lighthouse-back/src/Services/Notifications/WebhookHeaders.cs`
- Test: `lighthouse-back/lighthouse-back.Tests/Services/WebhookUrlTests.cs`
- Test: `lighthouse-back/lighthouse-back.Tests/Services/WebhookHeadersTests.cs`

**Interfaces:**
- Produces:
  - `public static class WebhookUrl { public static bool IsValid(string? value); }`
  - `public static class WebhookHeaders {`
    `public const string MaskedValue = "••••••••";`
    `public static bool TryParse(string? text, out IReadOnlyList<KeyValuePair<string,string>> headers, out string? error);`
    `public static string Mask(string? value); }`
  - `TryParse(null/""/whitespace)` → `true`, empty list. On failure `error` names the 1-based line number and why (e.g. `"Line 2: missing ':'"`).

- [ ] **Step 1: Write the failing tests**

`WebhookUrlTests`:
- `IsValid_ReturnsTrue` theory: `http://apprise:8000/notify/homelab`, `https://example.com/hook`, `http://192.168.1.10:8080/x?y=1`.
- `IsValid_ReturnsFalse` theory: `null`, `""`, `"   "`, `ftp://host/x`, `not-a-url`, `/relative/path`.

`WebhookHeadersTests`:
- `TryParse_ReturnsEmpty_ForNullOrBlank` (theory `null`, `""`, `"\n  \n"`) → `true`, `headers.Should().BeEmpty()`.
- `TryParse_ParsesMultipleLines`: `"Authorization: Bearer abc\nX-Api-Key: k1"` → `[("Authorization","Bearer abc"),("X-Api-Key","k1")]`.
- `TryParse_KeepsColonsInValue`: `"X-Callback: http://host:1234/x"` → value `"http://host:1234/x"`.
- `TryParse_HandlesCrLf`: `"A: b\r\nC: d\r\n"` → `[("A","b"),("C","d")]`.
- `TryParse_IgnoresBlankLines`: `"A: b\n\n   \nC: d"` → 2 headers.
- `TryParse_Fails_WhenColonMissing`: `"A: b\nnocolon"` → `false`, `error.Should().Contain("2")`.
- `TryParse_Fails_ForInvalidName` theory: `": v"`, `"Bad Name: v"`, `"Bad(Name): v"` → `false`.
- `TryParse_Fails_ForForbiddenName` theory: `"Content-Type: x"`, `"content-length: 1"`, `"HOST: h"` → `false`.
- `Mask_ReturnsDots_WhenNonEmpty`: `Mask("A: b")` → `"••••••••"`; `Mask(null)` / `Mask("")` → `""`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~WebhookUrlTests|FullyQualifiedName~WebhookHeadersTests"`
Expected: build FAILS (types not defined).

- [ ] **Step 3: Implement `WebhookUrl.IsValid` and `WebhookHeaders.TryParse` / `Mask`**

`IsValid`: `Uri.TryCreate(..., UriKind.Absolute)` and scheme is `http` or `https`. `TryParse`: split on `'\n'`, trim each line (removes `\r`), skip empty; name token check = non-empty and every char is an RFC 7230 `tchar` (`!#$%&'*+-.^_`|~`, digits, letters). Style mirrors `DiscordWebhookUrl.cs` (XML doc comments).

- [ ] **Step 4: Run tests to verify they pass**

Run: same filter. Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add lighthouse-back/lighthouse-back/src/Services/Notifications/Webhook*.cs lighthouse-back/lighthouse-back.Tests/Services/Webhook*Tests.cs
git commit -m "feat(notifications): add webhook URL and header validation helpers"
```

---

### Task 2: Shared formatting helpers (refactor)

**Files:**
- Create: `lighthouse-back/lighthouse-back/src/Services/Notifications/NotificationFormatting.cs`
- Modify: `lighthouse-back/lighthouse-back/src/Services/Notifications/DiscordNotificationService.cs` (remove private `FormatBytes`, `FormatImageRef`, `Truncate`; call the shared ones)

**Interfaces:**
- Produces: `internal static class NotificationFormatting { string FormatBytes(long bytes); string FormatImageRef(string entry); string Truncate(string value, int max); }` — bodies moved verbatim.

- [ ] **Step 1: Run Discord tests to record baseline**

Run: `dotnet test --filter "FullyQualifiedName~DiscordNotificationServiceTests"` — Expected: PASS.

- [ ] **Step 2: Move the three methods into `NotificationFormatting` and update Discord call sites**

- [ ] **Step 3: Re-run Discord tests**

Same command. Expected: PASS, no test file changed.

- [ ] **Step 4: Commit**

```bash
git add lighthouse-back/lighthouse-back/src/Services/Notifications/
git commit -m "refactor(notifications): share formatting helpers between channels"
```

---

### Task 3: WebhookNotificationService

**Files:**
- Create: `lighthouse-back/lighthouse-back/src/Services/Notifications/WebhookNotificationService.cs`
- Test: `lighthouse-back/lighthouse-back.Tests/Services/WebhookNotificationServiceTests.cs`

**Interfaces:**
- Consumes: `WebhookUrl`, `WebhookHeaders` (Task 1), `NotificationFormatting` (Task 2).
- Produces:
  - `public class WebhookNotificationService : INotificationService`
  - ctor `(HttpClient httpClient, AppDbContext db, ILogger<WebhookNotificationService> logger)`
  - `public const string EnabledKey = "NotificationsWebhookEnabled"; UrlKey = "NotificationsWebhookUrl"; HeadersKey = "NotificationsWebhookHeaders";`
  - `public Task<NotificationTestResult> SendTestAsync(string? overrideUrl, string? overrideHeaders, CancellationToken ct = default)` — `overrideUrl` null/blank → saved URL; `overrideHeaders` **null** → saved headers, `""` → no headers. Sends even when disabled. No URL → `NotificationTestResult(false, "No webhook URL configured.")`.
  - Note: the interface's existing `SendTestAsync(string?, CancellationToken)` is removed in Task 4. Until then implement it as `=> SendTestAsync(overrideWebhookUrl, null, ct)` so the build stays green.

Content per event (titles identical to Discord):

| Method | event | type | body | data |
|---|---|---|---|---|
| `NotifyAppUpdateStartedAsync(from,to)` | `app.update.started` | `info` | `Updating from {from} to {to}. The application will restart shortly.` | `{ fromVersion, toVersion }` |
| `NotifyAppUpdateFailedAsync(from,to,error)` | `app.update.failed` | `failure` | `Could not start the update from {from} to {to}.\nError: {error}` | `{ fromVersion, toVersion, error }` |
| `NotifyAppUpdateCompletedAsync(v)` | `app.update.completed` | `success` | `The application restarted and is now running {v}.` | `{ version }` |
| `NotifyComposeAutoUpdateAsync(report)` (skip when `!HasEntries`) | `compose.autoupdate` | failed==0 → `success`; updated>0 && failed>0 → `warning`; updated==0 → `failure` | per project: `{✅|❌} {name}` then `  • {svc} ({image}): {old} → {new}` (or `updated`), `  ⚠ {error}`, or `  Updated.`/`  Failed.` when no lines | `{ updatedCount, failedCount, projects:[{ name, success, error, services:[{ name, image, oldDigest, newDigest }] }] }` — all projects |
| `NotifyImagePruneAsync(images,bytes,dangling)` (skip when empty) | `images.prune` | `success` | `Reclaimed {FormatBytes} · {dangling images only|all unused images}.` then up to 20 `  • {FormatImageRef}` lines and `… +N more` | `{ images (raw, all), count, spaceReclaimedBytes, danglingOnly }` |
| `SendTestAsync` | `test` | `info` | `Webhook notifications are configured correctly for Lighthouse.` | `{}` |

Sending: `HttpRequestMessage` POST with `StringContent(json, UTF8, "application/json")`; each parsed header via `request.Headers.TryAddWithoutValidation`, falling back to `request.Content.Headers.TryAddWithoutValidation`. Non-2xx → throw `HttpRequestException($"Webhook returned {code}: {Truncate(body,200)}")`. Saved headers that fail `TryParse` → log warning and skip the send. Notify methods: read enabled/URL per call, no-op with debug log when disabled or URL empty, catch-all + `LogWarning` (never log header values).

- [ ] **Step 1: Write the failing tests**

Copy the `CreateInMemoryDbContext` / seed / `RecordingHandler` pattern from `DiscordNotificationServiceTests`, extending the handler to record `RequestUri` and headers per request, and to optionally throw a given exception. Seed helper takes `(bool? enabled, string? url, string? headers)`. Parse bodies with `JsonDocument`.

- `Notify_DoesNothing_WhenDisabled` → `CallCount == 0`.
- `Notify_DoesNothing_WhenUrlMissing` → `CallCount == 0`.
- `AppUpdateStarted_SendsExpectedPayload`: url `http://apprise:8000/notify/homelab`; assert request URI, `title == "🚀 Application update started"`, `type == "info"`, `event == "app.update.started"`, `data.fromVersion == "1.0.0"`, `data.toVersion == "1.1.0"`, `timestamp` parses as `DateTimeOffset`.
- `AppUpdateFailed_SendsFailureWithError`: `type == "failure"`, `data.error == "boom"`, body contains `"boom"`.
- `AppUpdateCompleted_SendsSuccess`: `type == "success"`, `data.version == "1.1.0"`.
- `ComposeAutoUpdate_TypeMapping` theory `(updated, failed, expectedType)`: `(2,0,"success")`, `(1,1,"warning")`, `(0,2,"failure")`.
- `ComposeAutoUpdate_IncludesProjectsAndServices`: 1 project `coder` with service `coder`/`ghcr.io/coder/coder:latest`/`a1b2c3d4e5f6`→`0f9e8d7c6b5a` and 1 failed project `authelia` with error `"pull failed"` → `data.projects[0].services[0].oldDigest == "a1b2c3d4e5f6"`, `data.projects[1].error == "pull failed"`, `data.projects[0].error` is JSON `null` (`ValueKind == Null`), body contains `"✅ coder"` and `"❌ authelia"`.
- `ComposeAutoUpdate_IncludesAllProjects_BeyondDiscordLimit`: 30 projects → `data.projects` length 30.
- `ComposeAutoUpdate_DoesNothing_WhenReportEmpty`.
- `ImagePrune_SendsAllImagesInData`: 25 images → `data.images` length 25, `data.count == 25`, body contains `"… +5 more"`.
- `Notify_SendsSavedHeaders`: headers `"Authorization: Bearer abc\nX-Api-Key: k1"` → request has both.
- `Notify_DoesNotThrow_WhenReceiverReturns500`.
- `Notify_DoesNotThrow_WhenRequestTimesOut`: handler throws `TaskCanceledException`.
- `SendTest_UsesOverrideUrl_EvenWhenDisabled` → success, request URI == override, `event == "test"`.
- `SendTest_UsesOverrideUrl_WithSavedHeaders`: saved headers `"X-Api-Key: k1"`, `overrideHeaders: null` → header sent.
- `SendTest_EmptyOverrideHeaders_SendsNoHeaders`: saved `"X-Api-Key: k1"`, `overrideHeaders: ""` → header absent.
- `SendTest_ReturnsError_WhenNoUrl` → `Success == false`, `Error == "No webhook URL configured."`.
- `SendTest_ReturnsError_WhenReceiverRejects` (400) → `Success == false`, `Error` contains `"400"`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~WebhookNotificationServiceTests"` — Expected: build FAILS.

- [ ] **Step 3: Implement `WebhookNotificationService`**

Serializer: `JsonNamingPolicy.CamelCase`, **no** `WhenWritingNull`. A private record `WebhookPayload(string Title, string Body, string Type, string Event, DateTime Timestamp, object Data)`; `data` via anonymous objects. Timestamp `DateTime.UtcNow`.

- [ ] **Step 4: Run tests to verify they pass** — same filter, all PASS. Also run the full suite: `dotnet test` → PASS.

- [ ] **Step 5: Commit**

```bash
git add lighthouse-back/lighthouse-back/src/Services/Notifications/WebhookNotificationService.cs lighthouse-back/lighthouse-back.Tests/Services/WebhookNotificationServiceTests.cs
git commit -m "feat(notifications): add generic webhook notification channel"
```

---

### Task 4: Composite fan-out and DI wiring

**Files:**
- Create: `lighthouse-back/lighthouse-back/src/Services/Notifications/CompositeNotificationService.cs`
- Modify: `lighthouse-back/lighthouse-back/src/Services/Notifications/INotificationService.cs` (remove `SendTestAsync`; update the "currently Discord" doc comment)
- Modify: `lighthouse-back/lighthouse-back/src/Services/Notifications/DiscordNotificationService.cs` (`SendTestAsync` stays as a public class method, unchanged signature)
- Modify: `lighthouse-back/lighthouse-back/src/Services/Notifications/WebhookNotificationService.cs` (drop the temporary 2-arg `SendTestAsync` bridge)
- Modify: `lighthouse-back/lighthouse-back/src/Extensions/ServiceCollectionExtensions.cs:120-128`
- Modify: `lighthouse-back/lighthouse-back/src/Controllers/NotificationsController.cs` (inject `DiscordNotificationService` instead of `INotificationService` so it still compiles; endpoint unchanged)
- Test: `lighthouse-back/lighthouse-back.Tests/Services/CompositeNotificationServiceTests.cs`

**Interfaces:**
- Produces:
  - `public class CompositeNotificationService : INotificationService`, ctor `(IReadOnlyList<INotificationService> channels, ILogger<CompositeNotificationService> logger)`. Each `Notify*` runs all channels via `Task.WhenAll`, each call wrapped in try/catch + `LogWarning`.
  - DI:
    ```csharp
    services.AddHttpClient<DiscordNotificationService>();
    services.AddHttpClient<WebhookNotificationService>(c => c.Timeout = TimeSpan.FromSeconds(10));
    services.AddScoped<INotificationService>(sp => new CompositeNotificationService(
        new INotificationService[] {
            sp.GetRequiredService<DiscordNotificationService>(),
            sp.GetRequiredService<WebhookNotificationService>() },
        sp.GetRequiredService<ILogger<CompositeNotificationService>>()));
    ```
  - `NotificationsController` ctor `(DiscordNotificationService discord, ILogger<NotificationsController> logger)` (Task 5 adds the webhook service).

- [ ] **Step 1: Write the failing tests** (two `Mock<INotificationService>`)

- `Notify_CallsAllChannels`: `NotifyAppUpdateStartedAsync("1","2")` → both mocks verified once with `("1","2")`.
- `Notify_CallsAllChannels_WhenOneThrows`: first mock's `NotifyComposeAutoUpdateAsync` throws `InvalidOperationException` (both synchronously via `.Throws` and as faulted task via `.ThrowsAsync` — two cases) → second still called, composite does not throw.
- `Notify_RunsChannelsConcurrently`: first mock returns a `TaskCompletionSource.Task` not yet completed; start `NotifyAppUpdateStartedAsync` without awaiting; assert second mock already invoked; then complete the TCS and await.
- One forwarding test each for `NotifyAppUpdateFailedAsync`, `NotifyAppUpdateCompletedAsync`, `NotifyImagePruneAsync` (arguments passed through).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CompositeNotificationServiceTests"` — Expected: build FAILS.

- [ ] **Step 3: Implement composite, remove `SendTestAsync` from the interface, update DI and controller ctor**

- [ ] **Step 4: Run full suite**

Run: `dotnet build -c Release && dotnet test` — Expected: build succeeds, all PASS.

- [ ] **Step 5: Commit**

```bash
git add lighthouse-back/
git commit -m "feat(notifications): fan out notifications to Discord and webhook concurrently"
```

---

### Task 5: API — webhook test endpoint, settings validation and masking

**Files:**
- Modify: `lighthouse-back/lighthouse-back/src/Controllers/NotificationsController.cs`
- Modify: `lighthouse-back/lighthouse-back/src/Controllers/ConfigController.cs` (`GetSettings` ~L228, `UpdateSetting` ~L246-285, `ValidateAutoUpdateSetting` ~L376-430)
- Test: `lighthouse-back/lighthouse-back.Tests/Controllers/NotificationsControllerTests.cs`
- Test: `lighthouse-back/lighthouse-back.Tests/Controllers/ConfigControllerSettingsTests.cs`

**Interfaces:**
- Consumes: `WebhookUrl`, `WebhookHeaders` (Task 1), `WebhookNotificationService` + keys + `SendTestAsync(url, headers, ct)` (Task 3).
- Produces:
  - `POST /api/notifications/test/webhook`, body `public record TestWebhookRequest(string? Url = null, string? Headers = null);` → 200 `ApiResponse.Ok<object>(null, "Test notification sent")`; 400 `"Invalid webhook URL"` when `Url` non-blank and `!WebhookUrl.IsValid`; 400 with the `TryParse` error when `Headers` non-null and invalid; 400 with `result.Error` on send failure.
  - `NotificationsController` ctor `(DiscordNotificationService discord, WebhookNotificationService webhook, ILogger<NotificationsController> logger)`.
  - `ValidateAutoUpdateSetting`: `EnabledKey` → bool; `UrlKey` → `WebhookUrl.IsValid` (message: `"Invalid webhook URL. Expected an absolute http(s) URL"`); `HeadersKey` → `TryParse` error message.
  - `GetSettings`: `HeadersKey` value → `WebhookHeaders.Mask(value)`.
  - `UpdateSetting`: for `HeadersKey`, log `"Setting updated: {Key}"` without the value, and return a **new** `AppSetting` copy with `Value = WebhookHeaders.Mask(...)` (do not mutate the tracked entity).

- [ ] **Step 1: Write the failing tests**

`NotificationsControllerTests` — construct real services over InMemory db + a stub `HttpMessageHandler` (same pattern as Task 3):
- `TestWebhook_ReturnsBadRequest_ForInvalidUrl`: `Url = "ftp://x"` → `BadRequestObjectResult`, handler not called.
- `TestWebhook_ReturnsBadRequest_ForInvalidHeaders`: `Url` valid, `Headers = "nocolon"` → 400.
- `TestWebhook_ReturnsOk_WhenReceiverAccepts`: `Url = "http://apprise:8000/notify/homelab"` → `OkObjectResult`.
- `TestWebhook_ReturnsBadRequest_WhenReceiverFails` (500) → 400.
- `TestDiscord_StillValidatesDiscordUrl`: existing `POST test` with `https://evil.com/...` → 400.

`ConfigControllerSettingsTests` — `new ConfigController(db, NullLogger<ConfigController>.Instance, null!, new ConfigurationBuilder().Build())` (settings endpoints don't touch `LogLevelService`):
- `GetSettings_MasksWebhookHeaders`: seed headers `"Authorization: Bearer secret"` → returned value `"••••••••"`; seeded webhook URL returned in clear.
- `UpdateSetting_DoesNotEchoWebhookHeaders`: PUT headers `"Authorization: Bearer secret"` → response `Data.Value == "••••••••"`, DB holds the raw value.
- `UpdateSetting_RejectsInvalidWebhookHeaders`: `"Content-Type: x"` → 400, nothing saved.
- `UpdateSetting_RejectsInvalidWebhookUrl`: `"not-a-url"` → 400.
- `UpdateSetting_AllowsEmptyWebhookUrlAndHeaders`: `""` for both → 200.
- `UpdateSetting_RejectsNonBoolWebhookEnabled`: `"yes"` → 400.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~NotificationsControllerTests|FullyQualifiedName~ConfigControllerSettingsTests"` — Expected: FAIL / build fails.

- [ ] **Step 3: Implement the endpoint and the `ConfigController` changes**

- [ ] **Step 4: Run full suite** — `dotnet test` → all PASS.

- [ ] **Step 5: Commit**

```bash
git add lighthouse-back/
git commit -m "feat(notifications): webhook test endpoint and settings validation/masking"
```

---

### Task 6: Frontend — Webhook card

**Files:**
- Modify: `lighthouse-front/src/lib/api/notifications.ts`
- Modify: `lighthouse-front/src/lib/stores/notifications.svelte.ts`
- Modify: `lighthouse-front/src/routes/(protected)/settings/+page.svelte` (script ~L214-412, markup after the Discord `</Card>` ~L1002)
- Modify: `lighthouse-front/src/lib/i18n/en.ts`, `fr.ts`, `es.ts` (`settings.notifications`)

**Interfaces:**
- Consumes: `POST /notifications/test/webhook` `{ url?, headers? }` (Task 5); settings keys (Global Constraints).
- Produces:
  - `notificationsApi.testWebhook(url?: string, headers?: string): Promise<void>`
  - `NOTIF_KEYS.webhookEnabled | webhookUrl | webhookHeaders`
  - `notificationsState.webhookEnabled: boolean`, `webhookUrl: string`, `webhookHeaders: string` (masked), loaded in `loadNotificationSettings`
  - `testGenericWebhook(url?: string, headers?: string): Promise<{ ok: boolean; error?: string }>`
  - i18n keys under `settings.notifications`: `genericTitle`, `genericDescription`, `genericEnableLabel`, `genericUrl`, `genericUrlPlaceholder` (`http://apprise:8000/notify/homelab`), `genericUrlHelp`, `genericHeaders`, `genericHeadersPlaceholder` (`Authorization: Bearer <token>`), `genericHeadersHelp`. Test button reuses `testButton` / `testing` / `testSuccess` / `testFailed`.

Card behaviour (copy the Discord card's markup/classes): toggle saves on change (revert on failure); URL input `id="generic-webhook-url"` with `genericUrlDirty`, saves trimmed value on blur when dirty; headers `<textarea id="generic-webhook-headers" rows="3">` monospace with `genericHeadersDirty`, saves on blur when dirty then reloads settings (to show the mask); Test button sends `url` only if `genericUrlDirty`, `headers` only if `genericHeadersDirty` (otherwise `undefined` → server uses saved), disabled while testing or when no URL (saved or typed). Help text (`genericDescription`) says: Apprise-compatible (`title`/`body`/`type`) and includes `event`/`data` for other tools; works independently of Discord. `genericHeadersHelp` says: one `Name: Value` per line, hidden after saving.

- [ ] **Step 1: Add API function, store keys/state/load/test function**

- [ ] **Step 2: Add i18n keys in `en.ts`, `fr.ts`, `es.ts`**

- [ ] **Step 3: Add the Webhook card and its handlers to the settings page**

- [ ] **Step 4: Type-check and build**

Run (from `lighthouse-front/`): `npm run check && npm run build` — Expected: 0 errors, build succeeds.

- [ ] **Step 5: Commit**

```bash
git add lighthouse-front/src
git commit -m "feat(notifications): webhook settings card in the UI"
```

---

### Task 7: End-to-end check

- [ ] **Step 1: Run backend + frontend locally** (`dotnet run --project lighthouse-back --launch-profile http`, `npm run dev`), log in as admin, open Settings → Notifications.

- [ ] **Step 2: Point the webhook at a capture receiver** (e.g. `docker run --rm -p 8080:8080 mendhak/http-https-echo`) with URL `http://localhost:8080/notify/homelab` and headers `X-Api-Key: k1`; click Test.
Expected: echo log shows POST with `X-Api-Key: k1`, JSON with `title`/`body`/`type`/`event:"test"`/`timestamp`/`data:{}`. Reload page: headers show `••••••••`, URL in clear.

- [ ] **Step 3: Against Apprise** (`docker run --rm -p 8000:8000 caronc/apprise` with a config key `homelab` pointing at any service, or use the requester's `http://apprise:8000/notify/homelab`) click Test.
Expected: 200 from Apprise, notification delivered.

- [ ] **Step 4: Disable Discord, enable webhook, trigger a dev auto-update or prune** (dev page `/dev` → force outdated images) and confirm only the webhook receives `compose.autoupdate`.

- [ ] **Step 5: Report results to the user** (no commit unless fixes were needed).
