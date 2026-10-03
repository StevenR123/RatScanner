# Rat Scanner — Scanning Reliability & Price Data Master Analysis

> **Scope:** Why scanning sometimes fails or returns wrong matches, and exactly when/how the app reaches
> external services (especially price data). This document is a *plan of record*: every issue is tied to a
> concrete code location, and every issue has at least one concrete remediation option.

---

## 1. Executive summary

Scanning accuracy in Rat Scanner is **not** gated by any confidence threshold today. The app computes a
confidence score for every scan (`Inspection.MarkerConfidence`, `Icon.DetectionConfidence`) but never
compares it against the configured warning thresholds, and never surfaces it to the user. Combined with a
number of "fail silently" code paths, this is the dominant reason scanning feels unreliable: when the OCR /
template matcher is unsure, the app either shows the best-guess result anyway or shows nothing at all.

On the price side the good news is that **prices are already bundled into the item payload** fetched from
`json.tarkov.dev` — there is no per-scan price lookup. The bad news is that price refresh is **purely
lazy**: the market data is re-fetched only when the 1-hour TTL expires *and* a scan/search happens to touch
it, and the declared `_marketDBRefreshTimer` is never started. So the app is **not** currently "cache all
prices at start + refresh on a schedule" — it is "cache on first use + refresh lazily on expiry", which is
close but missing the proactive scheduler the user wants.

---

## 2. How scanning works today (pipeline)

Both scan types are triggered by global low-level keyboard/mouse hooks
([UserActivityHelper.cs](RatScanner/UserActivityHelper.cs), installed in
[HotkeyManager.cs](RatScanner/HotkeyManager.cs)), fire on the hotkey thread, and enqueue a result into a
single `ItemQueue` ([ItemQueue.cs](RatScanner/Scan/ItemQueue.cs)) that the Blazor overlay polls every 100ms.

### 2.1 Name scan (left-click on the inspect magnifier)

Flow in [RatScannerMain.NameScan](RatScanner/RatScannerMain.cs#L209):

1. `Thread.Sleep(50)` to let the game draw the inspection tooltip after the click.
2. Capture a `(MarkerScanSize + TextWidth) × MarkerScanSize` region around the cursor
   (`50 + 600 = 650px` wide × `50px` tall, × game scale) via `CopyFromScreen`.
3. `RatEyeEngine.NewInspection(screenshot)` performs OCR + marker detection.
4. Gate: `if (!inspection.ContainsMarker || inspection.Item == null) return;`
   — **note there is no confidence check**.
5. Builds an `ItemNameScan`, looks up the item in `TarkovDevAPI.GetItems()`, enqueues it, repaints.

### 2.2 Full-screen name scan (auto-scan)

[RatScannerMain.NameScanScreen](RatScanner/RatScannerMain.cs#L249) captures the entire active monitor,
runs `NewMultiInspection`, and enqueues **every** inspection found.

### 2.3 Icon scan (Shift + left-click)

Flow in [RatScannerMain.IconScan](RatScanner/RatScannerMain.cs#L286):

1. Captures a `896 × 896px` (× scale) region centered on the cursor.
2. `RatEyeEngine.NewInventory(screenshot)` + `inventory.LocateIcon()` (template matching).
3. Gate: `if (icon?.DetectionConfidence <= 0 || icon?.Item == null) return;`
   — effectively only rejects a confidence of exactly `0`; the configured `0.8` threshold is ignored.
4. Builds an `ItemIconScan`, enqueues it, repaints.

### 2.4 Text scan (Ctrl + V) — implemented

Flow in [RatScannerMain.TextScan](RatScanner/RatScannerMain.cs#L326):

1. Captures a small `120 × 30px` (× scale) strip centred on the cursor — no grid parsing.
2. [TextScanProcessor.cs](RatScanner/Scan/TextScanProcessor.cs) upscales the strip 3×, binarizes it with an
   Otsu threshold, and OCRs it with Tesseract (same bender `traineddata` and `ToISO3Code()` language mapping
   as name scan).
3. The OCR text is sanitized (`CyrillicToLatin`, `I`→`T`, lowercase) and matched against `Item.ShortName`
   using normalized Levenshtein similarity, with a token-level fallback for neighbouring-cell leakage.
4. A result is only shown if the similarity is `>= TextScan.MinConfidence` (0.65); otherwise the scan is
   silently dropped (still subject to the silent-failure concern in 3.2).
5. The match is wrapped in an [ItemTextScan](RatScanner/Scan/ItemTextScan.cs) and enqueued into the same
   tooltip/overlay pipeline as name/icon scans.

Config lives in [RatConfig.TextScan](RatScanner/RatConfig.cs#L73) (enable flag + hotkey, persisted), is wired
through [HotkeyManager.cs](RatScanner/HotkeyManager.cs), and is exposed in
[SettingsScanning.razor](RatScanner/Pages/App/Settings/SettingsScanning.razor).

---

## 3. Issue catalog — scanning accuracy & reliability

### 3.1 Confidence thresholds are dead code  ⚠️ highest impact

- [RatConfig.cs](RatScanner/RatConfig.cs#L59) defines `NameScan.ConfWarnThreshold = 0.85f`.
- [RatConfig.cs](RatScanner/RatConfig.cs#L67) defines `IconScan.ConfWarnThreshold = 0.8f`.
- [ItemScan.cs](RatScanner/Scan/ItemScan.cs#L12) stores `Confidence`, and both
  [ItemNameScan.cs](RatScanner/Scan/ItemNameScan.cs#L14) and [ItemIconScan.cs](RatScanner/Scan/ItemIconScan.cs#L21)
  populate it.

**These values are never read anywhere.** A `grep` for `ConfWarnThreshold` and `Confidence` shows the only
consumers are the assignments themselves. The only runtime gate is:

- Name scan: [RatScannerMain.cs](RatScanner/RatScannerMain.cs#L227) — `ContainsMarker` + non-null item.
- Icon scan: [RatScannerMain.cs](RatScanner/RatScannerMain.cs#L300) — `DetectionConfidence <= 0`.

Result: a 30%-confidence OCR guess is displayed with exactly the same authority as a 99% match, and the
user is never warned.

**Remediation options**

1. Enforce the thresholds: drop results below `ConfWarnThreshold`, or render them with a distinct
   "low confidence" style instead of a normal tooltip.
2. Surface confidence in the tooltip (e.g. a small `%` badge or colour coding).
3. Expose both thresholds in the settings UI (currently they are not configurable in
   [SettingsScanning.razor](RatScanner/Pages/App/Settings/SettingsScanning.razor)).
4. Add a dedicated "minimum confidence to show" setting plus a "warn but show" band.

### 3.2 Silent failure paths swallow the reason scanning failed

Every hotkey callback is wrapped in `Wrap(...)` which logs the exception and continues
([HotkeyManager.cs](RatScanner/HotkeyManager.cs#L47)). Inside the scan methods, almost all failure modes are
plain `return;` statements with no user-visible signal:

- Name scan: `if (!inspection.ContainsMarker || inspection.Item == null) return;`
- Icon scan: `if (icon?.DetectionConfidence <= 0 || icon?.Item == null) return;`
- Screenshot failure: caught and logged, returning a black bitmap
  ([RatScannerMain.cs](RatScanner/RatScannerMain.cs#L314)).

From the user's point of view "nothing happened" — which is the #1 support symptom in the FAQ.

**Remediation options**

1. Centralise scan outcomes in an enum (`Success`, `NoMarker`, `LowConfidence`, `UnknownItem`,
   `ScreenshotFailed`, …) and log + optionally flash a subtle on-screen indicator.
2. Add a debug/diagnostics mode that surfaces the last scan's OCR text, marker position, confidence and
   captured screenshot (huge for community debugging).

### 3.3 Fixed 50 ms capture delay is a race condition

[RatScannerMain.cs](RatScanner/RatScannerMain.cs#L213) hard-codes `Thread.Sleep(50)` before capture, assuming
the inspection tooltip is rendered within 50 ms. On low FPS, a G-Sync/Freesync hiccup, or a slow menu the
tooltip is not yet drawn → `ContainsMarker == false` → silent no-result.

**Remediation options**

1. Replace the single capture with a short multi-frame capture loop (e.g. try 2–4 frames over ~150 ms) and
   use the first frame where the marker is detected with adequate confidence.
2. Make the delay adaptive (track recent success latency) or configurable in settings.
3. If no marker is found after retries, re-trigger once instead of dropping silently.

### 3.4 Item ID resolution can throw and abort the whole scan

Both [ItemNameScan.cs](RatScanner/Scan/ItemNameScan.cs#L13) and
[ItemIconScan.cs](RatScanner/Scan/ItemIconScan.cs#L19) do:

```csharp
TarkovDevAPI.GetItems().FirstOrDefault(item => item.Id == ratEyeId)
    ?? throw new Exception($"Unknown item: ...");
```

If RatEye's bundled item database (built in
[RatStashDatabaseFromTarkovDev](RatScanner/RatScannerMain.cs#L192) from `TarkovDevAPI.GetItems()`) is out of
sync with the OCR/template result — e.g. an item added in a newer game patch — the scan throws. `Wrap`
swallows it, so the user sees nothing and the queue is not repainted.

**Remediation options**

1. Do not throw. Instead fall back to a name/normalized-name lookup, then to a "unknown item" placeholder
   result rather than dropping the scan.
2. Keep `TarkovDevAPI` items and the RatEye database in lockstep: rebuild RatEye's `Database` immediately
   after every successful items refresh instead of only at startup.

### 3.5 `ItemQueue` pruning logic is inverted and leaks entries

[ItemQueue.OnChanged](RatScanner/Scan/ItemQueue.cs#L12):

```csharp
while (queue.Count > 1 && !(DateTimeOffset.Now.ToUnixTimeMilliseconds() > queue.First().DissapearAt)) {
    if (!queue.TryDequeue(out _)) break;
}
```

This dequeues the front item **while it is still valid** and stops as soon as the front item **is expired**.
The intended behaviour is the opposite: remove expired items. Consequences:

- Expired tooltips linger in the queue forever (they are filtered visually by the overlay's `DissapearAt`
  check, but the queue still grows).
- `First()` on a `ConcurrentQueue` is a linear scan, so the pruning pass is O(n²) as the queue grows.
- `LastItemScan` in [MenuVM.cs](RatScanner/ViewModel/MenuVM.cs#L25) can point at a stale entry, which is
  what the minimal UI binds to.

Also note the typo `DissapearAt` (should be `DisappearAt`), which propagates into the model.

**Remediation options**

1. Fix the condition to `while (queue.TryPeek(out var first) && now > first.DissapearAt) queue.TryDequeue(out _);`.
2. Rename `DissapearAt` → `DisappearAt` everywhere.
3. Add an explicit cap (e.g. drop oldest beyond N entries) as a safety net.

### 3.6 Resolution / DPI / HDR / multi-monitor assumptions

- Screen resolution and scale are read from Tarkov's `Graphics.ini` on startup
  ([RatConfig.TrySetScreenConfig](RatScanner/RatConfig.cs#L346)) and used to pick the RatEye scale
  ([RatScannerMain.cs](RatScanner/RatScannerMain.cs#L176), `Resolution2Scale`).
- If the user's in-game resolution differs from the saved config (borderless vs. exclusive, DLSS/FSR
  output resolution, display scaling changes), **the crop region and scale are wrong** and OCR/template
  matching degrades. The FAQ ("Nothing happens when scanning") already tells users to check resolution and
  disable HDR.
- `NameScanScreen` uses `Screen.AllScreens.First(screen => screen.Bounds.Contains(mousePosition))`
  ([RatScannerMain.cs](RatScanner/RatScannerMain.cs#L252)) — throws `InvalidOperationException` if the
  cursor is outside all screen bounds.
- Multi-monitor negative coordinates are handled inconsistently: capture uses raw virtual-screen
  coordinates, while the overlay offsets by the virtual-screen origin
  ([BlazorUI.xaml.cs](RatScanner/View/BlazorUI.xaml.cs#L45)). Any mismatch shifts tooltips off the item.

**Remediation options**

1. Detect the actual Tarkov window bounds (`FindWindow`/`GetWindowRect`) at scan time rather than trusting
   the saved config; fall back to config when the game window can't be found.
2. Validate that the saved resolution still matches the primary display before scanning.
3. Detect HDR (or a mismatch between config and reality) and warn the user explicitly.
4. Replace `First(...)` with `FirstOrDefault(...)` + a safe fallback.

### 3.7 `GetScreenshot` returns a black bitmap on failure

[GetScreenshot](RatScanner/RatScannerMain.cs#L314) catches `CopyFromScreen` exceptions, logs a warning, and
returns the (blank, uninitialised) bitmap. The subsequent inspection just fails with no marker and the scan
is silently dropped. A transient capture failure is indistinguishable from "there was nothing to scan".

**Remediation options**

1. Propagate a failure result instead of a black bitmap so the caller can show feedback / retry.
2. Retry capture once after a short delay before giving up.

### 3.8 Full-screen auto-scan is expensive and indiscriminate

`NameScanScreen` runs `NewMultiInspection` on the whole monitor and enqueues every detected inspection
([RatScannerMain.cs](RatScanner/RatScannerMain.cs#L249)). On a busy screen (stash full of items) this can
produce many overlapping/duplicate tooltips, each paying a `TarkovDevAPI.GetItems()` array scan and several
LINQ passes, and the whole thing runs on the shared `NameScanLock`.

**Remediation options**

1. De-duplicate/non-maximum-suppress overlapping inspections before enqueue.
2. Apply the same confidence threshold to multi-inspections.
3. Run OCR/template work off the hotkey thread and keep the lock only around the screenshot + enqueue.

### 3.9 Icon scan specific accuracy issues (partially known)

Known limitations acknowledged in [README.md](README.md) and [FAQ.md](FAQ.md):

- Items sharing an icon (keys, small attachments) produce uncertain matches — and because 3.1 leaves
  confidence unused, the wrong one is shown with no warning.
- Stash top-left lighting interference (the bright light at top-center of the screen) corrupts template
  matching.
- Rotated icons (`ScanRotatedIcons`) add a second hypothesis that can beat the correct one.
- The 896×896 capture region is large and fixed, so a click near screen edges crops to empty space.

**Remediation options**

1. Enforce the `0.8` icon threshold and visually mark "ambiguous — multiple candidates" results
   (RatEye already exposes `DetectionConfidence`; consider exposing candidate list if available).
2. Pre-compute a per-icon lighting/edge mask, or drop the top strip of the stash capture that the light
   hits (configurable).
3. When confidence is close between candidates, show the top-N alternates in the tooltip.

### 3.10 OCR language must match the game language

OCR language is driven by `RatConfig.NameScan.Language`
([RatScannerMain.cs](RatScanner/RatScannerMain.cs#L177)) and the API translation language is derived from it
([TarkovDevAPI.LanguageCode](RatScanner/TarkovDevAPI.cs#L56)). If the setting does not match the in-game
language, OCR misreads names and translations/lookups mismatch.

**Remediation options**

1. Add a first-run / settings prompt that confirms the game language.
2. Log the effective OCR language on every scan setup so misconfiguration is obvious in logs.

### 3.11 Expensive recomputation on every render

In [ItemSearchResult.razor](RatScanner/Pages/InteractableOverlay/Components/ItemSearchResult.razor#L16),
`GetTaskRemaining()` and `GetHideoutRemaining()` are called multiple times per item render; each call
iterates the full task and hideout station sets from [ItemExtensions.cs](RatScanner/ItemExtensions.cs#L18)
and [ItemExtensions.cs](RatScanner/ItemExtensions.cs#L90). `GetAmmoOfSameCaliber()`
([ItemExtensions.cs](RatScanner/ItemExtensions.cs#L119)) re-scans the entire item list. With tracking
enabled and a long search result list this produces visible UI lag immediately after a scan, which users
read as "scanning is slow/unreliable".

**Remediation options**

1. Memoise per-item task/hideout counts (invalidate on TarkovTracker refresh).
2. Pre-compute a `Dictionary<caliber, Item[]>` once per items refresh.
3. Cache `LastItem` derived values in `MenuVM` instead of recomputing on every property access.

### 3.12 Blocking network call on the scan path (cold cache)

[GetCached](RatScanner/TarkovDevAPI.cs#L251) does:

```csharp
Task.Run(() => QueueEndpointRequest<T>(...)).Wait();
return GetCached<T>(..., isRetry: true);
```

when an endpoint is not in cache. `ItemNameScan`/`ItemIconScan` constructors call `GetItems()` on the scan
path, so if the cache was never primed (startup fetch failed, or a new language/game-mode key is requested
for the first time) the **scan thread blocks on an HTTP round-trip**, and on failure it throws
`"Retrying to fetch query response failed."` — which `Wrap` silently swallows.

**Remediation options**

1. Never block the scan path. Return "no data yet" and schedule the fetch in the background; the next scan
   will have data.
2. Prime all language/game-mode keys the user can select at startup, or on settings save.
3. Make the startup fetch fully async with a graceful "offline" state instead of
   `InitializeCache().Wait()` ([RatScannerMain.cs](RatScanner/RatScannerMain.cs#L79)).

---

## 4. External services & price data flow

### 4.1 Service inventory

| Service | URL | Purpose | Trigger / cadence | Code |
|---|---|---|---|---|
| tarkov.dev JSON API | `https://json.tarkov.dev/{gameMode}/items` (+ `items_{lang}`, `tasks`, `hideout`, `maps`) | **Item metadata + prices** (avg24h, low24h, high24h, trader buy/sell), tasks, hideout, maps | Startup + lazy TTL refresh (items 1h, rest 12h) | [TarkovDevAPI.cs](RatScanner/TarkovDevAPI.cs) |
| RatScanner API | `https://api.ratscanner.com/v3/...` | Client version, force-update list, resource links, updater download, OAuth token refresh | Startup version check; updater download | [ApiManager.cs](RatScanner/ApiManager.cs) |
| TarkovTracker | `https://tarkovtracker.io/api/v2` or `https://api.tarkovtracker.org` | Quest/hideout progress (token, team/progress) | Startup + every 5 min timer | [TarkovTrackerDB.cs](RatScanner/TarkovTrackerDB.cs), [RatConfig.cs](RatScanner/RatConfig.cs#L108) |
| Discord OAuth | `https://discord.com/oauth2/...` + local `HttpListener` on `127.0.0.1:42252` | Patreon/Discord membership check | User-initiated auth | [AuthService.cs](RatScanner/AuthService.cs), [OAuth2.cs](RatScanner/OAuth2.cs) |
| tarkov.dev assets | `https://assets.tarkov.dev/...` | Trader avatars & item images (rendered by WebView2) | On-demand per image in the browser | [Item.cs](RatScanner/TarkovDev/Json/Item.cs#L150), [ItemSearchResult.razor](RatScanner/Pages/InteractableOverlay/Components/ItemSearchResult.razor) |
| Microsoft | `https://go.microsoft.com/fwlink/p/?LinkId=2124703` | WebView2 runtime installer | First launch only | [App.xaml.cs](RatScanner/App.xaml.cs#L77) |
| Fandom | `https://static.wikia.nocookie.net/.../Site-logo.png` | Logo image in main UI | On render | [Index.razor](RatScanner/Pages/App/Index.razor#L21) |

Local-only: `https://local.data/...` is a WebView2 virtual-host mapping to the local `Data` folder
([BlazorOverlay.xaml.cs](RatScanner/View/BlazorOverlay.xaml.cs#L61)), not a network call.

### 4.2 Where prices come from

Prices are **not** a separate endpoint. They arrive embedded in the `items` payload from
`json.tarkov.dev` and are deserialised straight into the item model
([Item.cs](RatScanner/TarkovDev/Json/Item.cs)):

- Market: `Avg24HPrice`, `Low24HPrice`, `High24HPrice`, `LastLowPrice`, `LastOfferCount`, `ChangeLast48H*`.
- Traders: `BuyFromTrader` / `SellToTrader` (`TraderPrice.PriceRub`), with `GetBestTraderOffer()`
  ([Item.cs](RatScanner/TarkovDev/Json/Item.cs#L147)) picking the max sell price.

So a single `items` fetch refreshes *all* prices for *all* items at once. There is no per-item/per-scan
network activity during normal scanning — the price the tooltip shows is read from the in-memory item.

### 4.3 Current caching model

- In-memory: `ConcurrentDictionary<string, (long expire, object response)>` in
  [TarkovDevAPI.cs](RatScanner/TarkovDevAPI.cs#L40).
- On-disk: whole JSON written to `%TEMP%\RatScanner\Cache\{sha256}.data`
  ([RatConfig.WriteToCache](RatScanner/RatConfig.cs#L334)), loaded on startup by
  [TryInitializeCacheFromOffline](RatScanner/TarkovDevAPI.cs#L274).
- TTLs ([RatConfig.cs](RatScanner/RatConfig.cs#L150)): `SuperShortTTL` 30s, `ShortTTL` 5m,
  `MediumTTL` 1h (items), `LongTTL` 12h (tasks/hideout/maps).
- Refresh trigger: **lazy only** — `GetCached` queues a background re-fetch when the TTL has expired
  ([TarkovDevAPI.cs](RatScanner/TarkovDevAPI.cs#L262)) and **returns the stale data immediately**
  (stale-while-revalidate). On failure it re-uses the old cache and extends the TTL by only 30s
  ([TarkovDevAPI.cs](RatScanner/TarkovDevAPI.cs#L125)), which can cause rapid retry churn while offline.

### 4.4 Gap analysis vs. "cache all prices at start + refresh on schedule"

**Verdict: the app does not currently follow the requested model.** It is close, but with three concrete
gaps:

1. **No scheduled market refresh.** `_marketDBRefreshTimer` is declared
   ([RatScannerMain.cs](RatScanner/RatScannerMain.cs#L29)) but **never instantiated or used**. Only the
   TarkovTracker timer ([RatScannerMain.cs](RatScanner/RatScannerMain.cs#L111)) runs on a schedule (5 min),
   and that is for quest progress, not prices.
2. **Refresh depends on scan/search activity.** If the user leaves the app running for hours without
   scanning, prices are refreshed only on the next scan that touches `GetItems()` — and because of
   stale-while-revalidate, the **first scan after expiry shows old prices** while the refresh completes in
   the background.
3. **Ephemeral cache location.** The offline cache lives in `%TEMP%\RatScanner\Cache`, which Windows temp
   cleaners and reboots can wipe, defeating "prime at startup and stay fresh".

---

## 5. Recommendations & solutions

### 5.1 Quick wins (low-risk bug fixes, high perceived reliability gain)

| # | Fix | Where |
|---|---|---|
| 1 | Enforce `ConfWarnThreshold` in both scan paths; drop or mark below-threshold results | [RatScannerMain.cs](RatScanner/RatScannerMain.cs#L227), [RatScannerMain.cs](RatScanner/RatScannerMain.cs#L300) |
| 2 | Fix the inverted `ItemQueue.OnChanged` pruning loop | [ItemQueue.cs](RatScanner/Scan/ItemQueue.cs#L13) |
| 3 | Replace `throw` on unknown item IDs with a fallback/placeholder scan | [ItemNameScan.cs](RatScanner/Scan/ItemNameScan.cs#L13), [ItemIconScan.cs](RatScanner/Scan/ItemIconScan.cs#L19) |
| 4 | Start `_marketDBRefreshTimer` on a schedule (see 5.3) | [RatScannerMain.cs](RatScanner/RatScannerMain.cs#L29) |
| 5 | Stop returning black bitmaps on capture failure; retry once, then report | [RatScannerMain.cs](RatScanner/RatScannerMain.cs#L314) |
| 6 | `FirstOrDefault` + fallback for the multi-screen bounds lookup | [RatScannerMain.cs](RatScanner/RatScannerMain.cs#L252) |

### 5.2 Reliability hardening

1. **Confidence policy.** Introduce a 3-band model:
   - `>= warn threshold` → normal tooltip;
   - `[min, warn)` → "low confidence" styled tooltip with the confidence %;
   - `< min` → dropped (configurable).
   Expose both thresholds in [SettingsScanning.razor](RatScanner/Pages/App/Settings/SettingsScanning.razor).
2. **Result feedback.** Return a scan result enum so failures can produce a visible but unobtrusive cue
   (e.g. a brief "⚠" at the cursor) instead of silence.
3. **Adaptive capture.** Replace the fixed `Thread.Sleep(50)` with a small capture loop (2–4 frames) that
   accepts the first frame with a confident marker; add a "re-trigger once on no-marker" fallback.
4. **Keep RatEye in sync.** Rebuild the RatEye `Database` from the freshest `TarkovDevAPI.GetItems()`
   after every successful items refresh, not only at startup
   ([RatScannerMain.cs](RatScanner/RatScannerMain.cs#L192)).
5. **Decouple scan from network.** Ensure the scan path never blocks on HTTP (see 3.12) and never throws
   due to missing cache; scans should degrade to "no price yet" rather than fail.
6. **Diagnostics mode.** Save the last screenshot + OCR text + confidence to the Debug folder when enabled,
   to make community bug reports actionable.

### 5.3 Price caching & scheduled refresh (the requested direction)

The user is right to move here. Recommended design:

1. **Prime all price-bearing data at startup (already mostly done).** Keep
   `TryInitializeCacheFromOffline` → background `InitializeCache`, but make the no-cache path asynchronous
   and non-fatal ([RatScannerMain.cs](RatScanner/RatScannerMain.cs#L72)).
2. **Start the market refresh timer.** Instantiate `_marketDBRefreshTimer` in the existing timer thread
   with a sensible default (suggest **30–60 minutes**, matching the 1h item TTL) that calls
   `TarkovDevAPI.InitializeCache()` or a dedicated items-only refresh. This makes prices proactive instead
   of lazy.
3. **Split TTLs / endpoints.** Because prices live inside `items`, a full items re-fetch also re-downloads
   static metadata. If freshness of prices matters more than metadata, either:
   - keep a single items endpoint but refresh on the shorter schedule; or
   - move to tarkov.dev's dedicated price surface (e.g. the GraphQL/`api.tarkov.dev` price endpoints) for
     the frequent refresh and merge prices into the cached item objects, leaving item metadata on the 12h
     schedule.
4. **Persist the cache somewhere durable.** Move `CacheDir` from `%TEMP%\RatScanner\Cache` to
   `%LOCALAPPDATA%\RatScanner\Cache` (or next to `config.cfg`) so the startup prime survives reboots and
   temp cleaners ([RatConfig.cs](RatScanner/RatConfig.cs#L39)).
5. **Surface price staleness.** Persist the fetch timestamp with the cache and display "price updated HH:MM"
   (already supported via `MinimalUi.ShowUpdated`, but only wired to the item's `Updated` field). During a
   background refresh, show a subtle "refreshing…" state instead of silently showing stale values.
6. **Backoff for offline/429.** Replace the fixed `SuperShortTTL` retry with exponential backoff to avoid
   hammering tarkov.dev while offline ([TarkovDevAPI.cs](RatScanner/TarkovDevAPI.cs#L125)).

### 5.4 Performance

1. Memoise `GetTaskRemaining` / `GetHideoutRemaining` per item, invalidated on TarkovTracker refresh
   ([ItemExtensions.cs](RatScanner/ItemExtensions.cs#L18)).
2. Pre-index items by caliber for `GetAmmoOfSameCaliber`
   ([ItemExtensions.cs](RatScanner/ItemExtensions.cs#L119)).
3. Cache derived `MenuVM` values on `LastItem` instead of recomputing on every binding
   ([MenuVM.cs](RatScanner/ViewModel/MenuVM.cs#L50)).
4. Move OCR/template work off the hotkey thread (or keep it but don't hold the global locks during image
   processing).

---

## 6. Proposed phased implementation plan

**Phase 1 — correctness quick wins (small, safe):**
Fix `ItemQueue` pruning; enforce confidence thresholds; replace unknown-item `throw`; start
`_marketDBRefreshTimer`; safe screen-bounds lookup; screenshot retry.

**Phase 2 — feedback & capture reliability:**
Scan result enum + visual cue; adaptive multi-frame capture; surface confidence in tooltips; diagnostics
mode.

**Phase 3 — price data architecture:**
Durable cache location; scheduled items/price refresh with stale-while-revalidate + staleness indicator;
exponential backoff; optionally split price refresh onto a dedicated price endpoint.

**Phase 4 — performance & sync:**
Per-item task/hideout memoisation; caliber index; off-thread OCR; RatEye database rebuild after refresh.

---

## 7. Appendix — key files

| Area | File |
|---|---|
| Scan orchestration | [RatScannerMain.cs](RatScanner/RatScannerMain.cs) |
| Hotkey triggers | [HotkeyManager.cs](RatScanner/HotkeyManager.cs), [ActiveHotkey.cs](RatScanner/ActiveHotkey.cs) |
| Scan results queue | [ItemQueue.cs](RatScanner/Scan/ItemQueue.cs), [ItemScan.cs](RatScanner/Scan/ItemScan.cs) |
| Text scan (new) | [TextScanProcessor.cs](RatScanner/Scan/TextScanProcessor.cs), [ItemTextScan.cs](RatScanner/Scan/ItemTextScan.cs) |
| Item lookups / derived data | [ItemExtensions.cs](RatScanner/ItemExtensions.cs), [Item.cs](RatScanner/TarkovDev/Json/Item.cs) |
| External data & cache | [TarkovDevAPI.cs](RatScanner/TarkovDevAPI.cs), [ApiManager.cs](RatScanner/ApiManager.cs), [APIClient.cs](RatScanner/APIClient.cs) |
| Progress tracking API | [TarkovTrackerDB.cs](RatScanner/TarkovTrackerDB.cs) |
| Auth | [AuthService.cs](RatScanner/AuthService.cs), [OAuth2.cs](RatScanner/OAuth2.cs) |
| Config / TTLs / cache paths | [RatConfig.cs](RatScanner/RatConfig.cs) |
| Overlay rendering | [Index.razor](RatScanner/Pages/Overlay/Index.razor), [ItemSearchResult.razor](RatScanner/Pages/InteractableOverlay/Components/ItemSearchResult.razor) |
| Settings UI (scanning) | [SettingsScanning.razor](RatScanner/Pages/App/Settings/SettingsScanning.razor), [SettingsVM.cs](RatScanner/ViewModel/SettingsVM.cs) |
