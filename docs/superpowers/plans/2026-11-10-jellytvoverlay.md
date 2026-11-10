# JellyTvOverlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A generic Android TV overlay renderer app (JellyTvOverlay) plus a host Jellyfin plugin that lets plugins show layouts over the Jellyfin player and receive the user's actions; move the JellyXR, Jellycap and JellySubSync panels onto it and add actor detail.

**Architecture:** Client plugins talk to the host plugin through a reflection-bound static API that uses only BCL types (JSON strings and delegates). The host detects playback triggers from `ISessionManager` events, arbitrates who holds the remote, and delivers layout operations to the app over a per-device long-poll. The app parses layout v1, renders it in overlay windows, runs all navigation locally (focus, scroll, stepper, pages, sequence) and POSTs actions back.

**Tech Stack:** C# net8.0, `Jellyfin.Controller` 10.9.11, xUnit; Android Java (source 8, no lambdas), plain SDK `build.sh` (no Gradle), `org.json`, JUnit 5 console launcher for JVM tests.

**Spec:** `docs/superpowers/specs/2026-11-10-jellytvoverlay-design.md`

## Global Constraints

- Plugins: `net8.0`, `Jellyfin.Controller` 10.9.11, `targetAbi` `10.9.0.0`, must keep working on 10.11 (follow `ServerCompat` / `LibraryCompat` patterns).
- App: build with a `build.sh` like `apps/JellyXR.TV/build.sh`; `-source 8 -target 8`; anonymous classes, no lambdas; min SDK 26, target SDK 33.
- App package id: `com.xjellys.tvoverlay` (new; JellyXR TV `com.xjellys.jellyxr.tv` is retired, not upgraded).
- Names: app folder `apps/JellyTvOverlay`, APK `JellyTvOverlay-<version>.apk`, plugin `plugins/Jellyfin.Plugin.JellyTvOverlay`, route prefix `JellyTvOverlay`.
- Layout format carries `"v": 1`. The app ignores unknown node types and unknown fields.
- Key press → visible focus/scroll/value change: local, never waits on the network.
- OK on an actor → actor detail visible ≤ 150 ms (preloaded).
- Pause → passive cast panel visible: no slower than today (~1 s). Double-pause window: 4000 ms (pause → play → pause, same item).
- Action → server response on LAN ≤ 300 ms typical.
- No blur or heavy effects; `dim` = flat translucent black layer.
- Text supports RTL (Hebrew); `dir: "auto"` resolves per text node from its content.
- Only one layout holds the remote per device at a time.
- No remote `PlayNow` from SubSync (crashes Jellyfin for Android TV; see JellyXR TV 1.2.2).
- Fallback when the host plugin is missing or the device has no app: JellyXR → `DisplayMessage` toasts, Jellycap → ffmpeg recap video, SubSync → nothing.
- Security (spec "Security"): HTTPS with normal certificate validation, no trust-all; `http://` only to private/LAN hosts (RFC 1918, 169.254/16, `.local`, localhost, IPv6 ULA/link-local), shown as "Not secured (local network)"; bare host → try `https://` first. Token only in the `Authorization` header, never in URLs or logs. Image requests carry the token only to the server origin; other hosts must be `https` and get no token. Host endpoints serve only devices with a session of the calling user (admins: any).
- Old JellyXR TV endpoints (`JellyXR/Cards`, `JellyXR/Companion/*`, `Jellycap/Overlay*`, `Jellycap/Companion/Heartbeat`, `JellySubSync/*` used by the app) stay working this release.

## Review Focus

1. **The player's session id changes** (the Jellyfin client reconnects) while the app stays up: layouts and triggers must keep reaching the same box. Everything is keyed by player **deviceId**, never sessionId. Test: `OverlayQueueTests.Reconnect_NewSessionId_SameDevice_KeepsQueue` (Task 2).
2. **Playback resumed from a phone or another remote** while an interactive layout is up: the layout closes and the remote goes back to the player. Test: `DispatcherTests.Resume_ClosesLayoutsWithCloseOnResume` (Task 4).
3. **BACK on the chooser:** nothing is shown, the remote goes back, and no client gets `chosen`. Test: `DispatcherTests.ChooserBack_NoChosenEvent` (Task 4).
4. **App restarts or the long-poll drops mid-delivery:** on reconnect with `since=0` the app gets a snapshot of the layouts currently shown, not closed ones and not a replay of history. Test: `OverlayQueueTests.Snapshot_AfterClose_OmitsClosed` (Task 2).
5. **Actor with no bio, no image, or no other titles in the library** (and Hebrew names): actor detail still renders, with no empty poster row, and every text node is `dir:auto`. Test: `ActorDetailBuilderTests.NoTitlesNoBio_RendersPlaceholder` (Task 13).

---

## File Structure

```
plugins/Jellyfin.Plugin.JellyTvOverlay/
  Plugin.cs, PluginServiceRegistrator.cs, meta.json, Configuration/{PluginConfiguration.cs,configPage.html}
  Layout/LayoutValidator.cs        validate + normalise layout JSON (pure)
  Delivery/OverlayQueue.cs         per-device op log, snapshot, long-poll wait (pure)
  Triggers/TriggerDetector.cs      progress samples -> triggers (pure)
  Host/OverlayHost.cs              public static BCL-only API for client plugins
  Host/Dispatcher.cs               client registry, trigger fan-out, offers/chooser, focus, action routing (pure)
  Host/SessionBridge.cs            IHostedService: ISessionManager events -> TriggerDetector -> Dispatcher
  Api/OverlayController.cs         Poll / Event / Heartbeat endpoints
plugins/shared/OverlayClient.cs    linked source: reflection binding to OverlayHost (compiled into each client)
tests/Jellyfin.Plugin.JellyTvOverlay.Tests/   xUnit
tests/Jellyfin.Plugin.JellyXR.Tests/          xUnit (cast + actor builders)
tests/Jellyfin.Plugin.Jellycap.Tests/         xUnit (recap overlay state machine)
tests/Jellyfin.Plugin.JellySubSync.Tests/     xUnit (box builder, pending switch)
apps/JellyTvOverlay/
  AndroidManifest.xml, build.sh, test.sh, RELEASE_NOTES.md, res/
  src/com/xjellys/tvoverlay/
    setup:   MainActivity.java, Prefs.java, Api.java, BootReceiver.java   (ported from JellyXR TV)
    model:   Layout.java, Node.java, KeyBinding.java                      (pure Java + org.json)
    logic:   FocusModel.java, ValueState.java, Pager.java, Sequencer.java (pure Java)
    render:  Renderer.java, OverlayWindow.java, ImageCache.java, Icons.java
    service: OverlayService.java (session follow + long-poll + events + passthrough)
  test/com/xjellys/tvoverlay/*Test.java
docs/jellytvoverlay/layout-v1.md   published contract
```

## Shared contract (used by several tasks)

**Layout v1 JSON** (written to `docs/jellytvoverlay/layout-v1.md` in Task 1):

- Layout: `v`=1, `id`, `owner` (set by the host), `focus` (`passive` default | `interactive` | `interactive+passthrough`), `ttlMs` (0 = until closed), `dim` (0–1), `parent` (layout id), `closeOn` (subset of `resume`,`stop`,`itemStart`,`seek`; default `["stop","itemStart"]`), `keys` (map key → binding), `preload` (array of layouts to cache without showing), `root` (node).
- Keys: `left` `right` `up` `down` `ok` `play` `back`. Binding: `{"op":"action","action":"<name>"}` or `{"op":"step","target":"<value node id>","by":<number>}`.
- Common node fields: `type`, `id`, `x` `y` `w` `h` (fractions of the parent box; omitted = flow inside the container), `pad`, `gap`, `bg` (`#AARRGGBB`), `radius` (fraction of screen height), `focusable`, `action`.
- `text`: `text`, `size` (fraction of screen height, default 0.03), `color`, `bold`, `maxLines`, `dir` (`auto`|`ltr`|`rtl`, default `auto`), `scroll` (bool; scrolls with ▲▼ when focused).
- `image`: `src` (absolute URL or server-relative path starting with `/`), `fit` (`cover`|`contain`), `round` (bool).
- `icon`: `name` from the bundled set: `play` `pause` `info` `back` `check` `clock` `person` `subtitles` `left` `right` `up` `down` `close`.
- `button`: `label`, `icon`, `action`, `enabled` (default true; false = greyed and not focusable).
- `row` / `column` / `stack`: `children`.
- `list`: `children`, `orientation` (`horizontal`|`vertical`); scrolls to keep focus visible.
- `pages`: `children`, `intervalMs` (0 = only ◀▶ when interactive).
- `sequence`: `children` (each with `durationMs`), `finishedAction`.
- `value`: `value`, `min`, `max`, `scale` (display divisor), `decimals`, `suffix`, `signed` (bool), `changedAction` (debounced 400 ms).

**App → host events** (`POST /JellyTvOverlay/Event`, body JSON): `{"deviceId","layoutId","type":"action|closed","action","elementId","value","reason"}`. `value` is a map of value-node id → number for the layout. `reason` is one of `back` `ttl` `finished` `replaced`.

**Host → client events** (JSON string passed to the client's handler):
- `{"type":"trigger","trigger":"itemStart|pause|resume|doublePause|seek|stop","deviceId","sessionId","itemId","positionTicks","userId"}`
- `{"type":"action","deviceId","sessionId","layoutId","action","elementId","value":{...}}`
- `{"type":"chosen","deviceId","sessionId","offerId","itemId","positionTicks"}`
- `{"type":"closed","deviceId","layoutId","reason"}`

A trigger handler may return `{"offer":{"id":"<offerId>","label":"<text>"}}` or `null`. Other handler results are ignored; clients act through `Show`/`Close`.

---

## Milestone 1: Host plugin

### Task 1: Plugin scaffold, test project, layout validator, published format doc

**Files:**
- Create: `plugins/Jellyfin.Plugin.JellyTvOverlay/{Jellyfin.Plugin.JellyTvOverlay.csproj,Plugin.cs,PluginServiceRegistrator.cs,meta.json,Configuration/PluginConfiguration.cs,Configuration/configPage.html,Layout/LayoutValidator.cs}`
- Create: `tests/Jellyfin.Plugin.JellyTvOverlay.Tests/{Jellyfin.Plugin.JellyTvOverlay.Tests.csproj,LayoutValidatorTests.cs}`
- Create: `docs/jellytvoverlay/layout-v1.md`

**Interfaces:**
- Produces: `public sealed record ValidatedLayout(string Id, string Focus, int TtlMs, string? Parent, IReadOnlyList<string> CloseOn, JsonObject Json)`; `public static class LayoutValidator { public static ValidatedLayout Validate(string json, string owner); }` throws `LayoutException : Exception` (message names the bad field). It sets `owner`, fills defaults, and ids are namespaced as `<owner>/<id>` unless they already start with `<owner>/`.
- Plugin GUID: new, generated once and written into `Plugin.cs` and `meta.json`. Config: `bool Enabled = true`.

- [ ] **Step 1: Copy `plugins/Jellyfin.Plugin.Template` to the new folder**, rename it, set the csproj like JellyXR's (no OnnxRuntime). Create the xUnit test csproj (`xunit` 2.9.2, `xunit.runner.visualstudio` 2.8.2, `Microsoft.NET.Test.Sdk` 17.11.1) with a ProjectReference to the plugin.
- [ ] **Step 2: Write failing tests** in `LayoutValidatorTests`:
  - `Defaults_Filled`: `{"v":1,"id":"a","root":{"type":"text","text":"x"}}` with owner `JellyXR` → `Id=="JellyXR/a"`, `Focus=="passive"`, `TtlMs==0`, `CloseOn` = `["stop","itemStart"]`, `Json["owner"]=="JellyXR"`.
  - `WrongVersion_Throws`: `"v":2` → `LayoutException` whose message contains `v`.
  - `BadFocus_Throws`: `"focus":"grab"` → throws.
  - `MissingRoot_Throws`.
  - `UnknownNodeType_Kept`: `{"type":"sparkle"}` inside `children` → no throw, kept in `Json`.
  - `StepBinding_UnknownTarget_Throws`: `keys.left = {op:"step",target:"nope",by:-250}` with no value node `nope` → throws.
  - `ParentNamespaced`: `"parent":"cast"` → `Parent=="JellyXR/cast"`.
- [ ] **Step 3: Run** `dotnet test tests/Jellyfin.Plugin.JellyTvOverlay.Tests` → FAIL (type missing).
- [ ] **Step 4: Implement `LayoutValidator.Validate`** with `System.Text.Json.Nodes`. Recurse `children` and `preload` (preloaded layouts are validated with the same owner).
- [ ] **Step 5: Run tests** → PASS. `dotnet build plugins/Jellyfin.Plugin.JellyTvOverlay` → 0 errors.
- [ ] **Step 6: Write `docs/jellytvoverlay/layout-v1.md`** from "Shared contract" above, with one full example (the actor detail layout B) and the event shapes.
- [ ] **Step 7: Commit** `JellyTvOverlay: plugin scaffold, layout v1 validator and format doc`.

### Task 2: Per-device delivery queue with snapshot and long-poll

**Files:**
- Create: `plugins/Jellyfin.Plugin.JellyTvOverlay/Delivery/OverlayQueue.cs`
- Test: `tests/Jellyfin.Plugin.JellyTvOverlay.Tests/OverlayQueueTests.cs`

**Interfaces:**
- Consumes: `ValidatedLayout` (Task 1).
- Produces:
  - `public sealed record OverlayOp(long Seq, string Op /*show|close*/, string LayoutId, JsonObject? Layout)`
  - `public sealed class OverlayQueue(TimeProvider clock)` with:
    - `long Show(string deviceId, ValidatedLayout l)` (show or replace by id)
    - `long Close(string deviceId, string layoutId)`
    - `IReadOnlyList<ValidatedLayout> Shown(string deviceId)`
    - `Task<IReadOnlyList<OverlayOp>> WaitAsync(string deviceId, long since, TimeSpan timeout, CancellationToken ct)`
    - `void Tick()` (expires layouts whose `ttlMs` passed and adds `close` ops)
  - Rules: `since == 0` or `since` older than the retained log (keep the last 200 ops per device) → returns a snapshot: one `show` op per currently shown layout, `Seq` = current head. Otherwise ops with `Seq > since`, waiting up to `timeout` if there are none.

- [ ] **Step 1: Write failing tests:**
  - `Show_ThenWait_ReturnsOp`
  - `Wait_BlocksUntilShow` (start `WaitAsync` with 5 s timeout, `Show` after it, task completes with 1 op)
  - `Wait_TimesOut_Empty`
  - `Snapshot_AfterClose_OmitsClosed` (show a, show b, close a; `WaitAsync(since:0)` → exactly one `show` for b)
  - `Replace_SameId_SingleShown`
  - `Ttl_Expires_EmitsClose` (FakeTimeProvider from `Microsoft.Extensions.TimeProvider.Testing` 8.10.0)
  - `Reconnect_NewSessionId_SameDevice_KeepsQueue` (the API takes deviceId only; assert ops shown before a "reconnect" are returned for the same deviceId with since=0)
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement**: per-device lock + `TaskCompletionSource` signalled on append.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `JellyTvOverlay: per-device overlay queue with snapshot and long-poll`.

### Task 3: Trigger detector

**Files:**
- Create: `plugins/Jellyfin.Plugin.JellyTvOverlay/Triggers/TriggerDetector.cs`
- Test: `tests/Jellyfin.Plugin.JellyTvOverlay.Tests/TriggerDetectorTests.cs`

**Interfaces:**
- Produces:
  - `public enum TriggerKind { ItemStart, Pause, Resume, DoublePause, Seek, Stop }`
  - `public sealed record Trigger(TriggerKind Kind, string DeviceId, string SessionId, string UserId, Guid ItemId, long PositionTicks)`
  - `public sealed class TriggerDetector(TimeProvider clock)` with:
    - `IReadOnlyList<Trigger> OnSample(string deviceId, string sessionId, string userId, Guid itemId, bool isPaused, long positionTicks)`
    - `IReadOnlyList<Trigger> OnStopped(string deviceId, string sessionId, string userId, Guid itemId, long positionTicks)`
  - Rules:
    - New item for a device → `ItemStart`.
    - Playing → paused → `Pause`. If a `Resume` for the same item happened ≤ 4000 ms earlier, emit `DoublePause` **instead of** `Pause`.
    - Paused → playing → `Resume`.
    - While playing, a position jump with |Δposition − Δwall| > 5 s → `Seek`.
    - Stop → `Stop`; state cleared.

- [ ] **Step 1: Write failing tests:**
  - `FirstSample_ItemStart`
  - `Pause_Then_Resume`
  - `PausePlayPause_Within4s_DoublePause` (only `DoublePause` on the second pause, no `Pause`)
  - `PausePlayPause_After4s_Pause`
  - `NewItem_ResetsDoublePause`
  - `Jump_Seek`
  - `RepeatedPausedSamples_NoDuplicate`
  - `Stop_ClearsState`
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `JellyTvOverlay: playback trigger detector`.

### Task 4: Dispatcher: clients, offers and chooser, focus, action routing, closeOn

**Files:**
- Create: `plugins/Jellyfin.Plugin.JellyTvOverlay/Host/Dispatcher.cs`
- Test: `tests/Jellyfin.Plugin.JellyTvOverlay.Tests/DispatcherTests.cs`

**Interfaces:**
- Consumes: `OverlayQueue` (Task 2), `Trigger` (Task 3), `LayoutValidator` (Task 1).
- Produces: `public sealed class Dispatcher(OverlayQueue queue, TimeProvider clock)` with:
  - `bool Register(string clientId, string displayName, Func<string, Task<string?>> onEvent)` (re-register replaces)
  - `Task OnTriggerAsync(Trigger t)`
  - `bool Show(string clientId, string deviceId, string layoutJson)` (false + logged when rejected)
  - `void Close(string clientId, string deviceId, string layoutId)`
  - `Task OnAppEventAsync(string deviceId, JsonObject evt)`
  - `string? HolderOf(string deviceId)` (layout id holding the remote)
  - `const int OfferWindowMs = 400`
- Rules:
  1. A trigger goes to every client as `{"type":"trigger",...}`. Handlers run in parallel. Offers returned within `OfferWindowMs` count; later answers are ignored.
  2. One offer → send `chosen` to that client at once. Two or more → `Show` a host layout `host/chooser` (`focus:interactive`, `closeOn:["resume","stop","itemStart"]`, a vertical `list` with one `button` per offer, label = offer label, `action` = `choose:<clientId>:<offerId>`, centred, `dim:0.4`).
  3. App event `action` on `host/chooser` with `choose:…` → close the chooser, then send `chosen` to that client. `closed` with reason `back` → close only.
  4. Focus: `Show` of an interactive layout is rejected if another interactive layout holds the device, unless the holder is the same client, or the new layout's `parent` chain reaches the holder. `host/chooser` closing frees the hold.
  5. Before fanning out a trigger, close every shown layout whose `closeOn` contains the trigger kind (camelCase) and send `closed` (reason = trigger name) to its owner.
  6. App `action` / `closed` events go to the layout's owner. A `closed` event also removes the layout from the queue.
- Tests use a fake client handler that records events and returns a configured offer.

- [ ] **Step 1: Write failing tests:**
  - `Trigger_FansOutToAll`
  - `SingleOffer_ChosenImmediately_NoChooser`
  - `TwoOffers_ShowChooserWithBothLabels` (assert the button labels "Cast" and "Sync subtitles", and each action)
  - `ChooserPick_ClosesChooser_SendsChosen`
  - `ChooserBack_NoChosenEvent`
  - `LateOffer_Ignored` (handler delays 600 ms)
  - `Interactive_Rejected_WhenOtherClientHolds`
  - `Interactive_AllowedForChildOfHolder` (`parent` = holder id)
  - `Resume_ClosesLayoutsWithCloseOnResume` (interactive layout with `closeOn:["resume"]` is closed, owner gets `closed` reason `resume`, `HolderOf` → null)
  - `AppAction_RoutedToOwnerOnly`
  - `HandlerThrows_OtherClientsStillGetTrigger`
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `JellyTvOverlay: dispatcher with offers, chooser and focus arbitration`.

### Task 5: Static host API, session bridge, HTTP endpoints, settings page

**Files:**
- Create: `plugins/Jellyfin.Plugin.JellyTvOverlay/Host/OverlayHost.cs`, `Host/SessionBridge.cs`, `Api/OverlayController.cs`
- Modify: `PluginServiceRegistrator.cs`, `Configuration/configPage.html`
- Test: `tests/Jellyfin.Plugin.JellyTvOverlay.Tests/OverlayHostTests.cs`

**Interfaces:**
- Consumes: `Dispatcher`, `OverlayQueue`, `TriggerDetector`.
- Produces (public, BCL types only; **this is the third-party contract**):
  ```csharp
  namespace Jellyfin.Plugin.JellyTvOverlay;
  public static class OverlayHost {
      public const int ProtocolVersion = 1;
      public static bool Register(string clientId, string displayName, Func<string, Task<string?>> onEvent);
      public static bool Show(string clientId, string deviceId, string layoutJson);
      public static void Close(string clientId, string deviceId, string layoutId);
      public static bool HasApp(string deviceId);   // heartbeat seen within 90 s
  }
  ```
  Before `SessionBridge` starts, calls are buffered or return false (registrations are kept and applied once the dispatcher exists).
- HTTP (all `[Authorize]`):
  - `GET JellyTvOverlay/Poll?deviceId=&since=` → `{"seq":n,"ops":[...]}`; holds up to 25 s.
  - `POST JellyTvOverlay/Event` → 204.
  - `POST JellyTvOverlay/Heartbeat?deviceId=` → `{"enabled":bool,"protocol":1}`.
  - `GET JellyTvOverlay/Clients` → registered clients (for the settings page).
- `SessionBridge : IHostedService` subscribes `PlaybackStart` / `PlaybackProgress` / `PlaybackStopped` and feeds `TriggerDetector` with `e.DeviceId`, `e.Session.Id`, `e.Session.UserId`, `e.Item.Id`, `e.IsPaused`, `e.PlaybackPositionTicks`. It also runs `queue.Tick()` every 500 ms.
- Settings page: `Enabled` toggle, list of registered clients, list of devices with a fresh heartbeat, each with an on/off switch (config `string[] DisabledDevices`). A disabled device: `HasApp` → false (clients fall back), `Show` → false, and Heartbeat returns `enabled:false`.

- Authorization: `public static class DeviceAccess { public static bool CanUse(Guid userId, bool isAdmin, string deviceId, IEnumerable<(string DeviceId, Guid UserId)> sessions); }` (pure). Poll, Event and Heartbeat return 403 unless `CanUse` holds (user from `HttpContext.User` claims, sessions from `ISessionManager.Sessions`). Event also returns 403 if `layoutId` is not currently shown on that device. Nothing logs headers or the token.

- [ ] **Step 1: Write failing tests:** `Register_BeforeStart_AppliedOnStart`; `HasApp_FalseAfter90s` (fake clock); `Show_Disabled_ReturnsFalse`; `DisabledDevice_HasAppFalse_ShowFalse`; `DeviceAccess_OtherUsersDevice_Denied`; `DeviceAccess_Admin_Allowed`; `DeviceAccess_NoSession_Denied`; `Event_LayoutNotShown_Rejected`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS; `dotnet build` → 0 errors.
- [ ] **Step 5: Manual check:** copy the DLL into a Jellyfin 10.9/10.10 test server, restart, and run `curl -H "Authorization: MediaBrowser Token=<t>" "<server>/JellyTvOverlay/Poll?deviceId=x&since=0"` → `{"seq":0,"ops":[]}` after ≤ 25 s. Pause a TV playback and confirm the plugin log shows the `pause` trigger.
- [ ] **Step 6: Commit** `JellyTvOverlay: host API, session bridge and HTTP endpoints`.

### Task 6: Shared client binder (linked source)

**Files:**
- Create: `plugins/shared/OverlayClient.cs`
- Test: `tests/Jellyfin.Plugin.JellyTvOverlay.Tests/OverlayClientTests.cs` (links the same file)

**Interfaces:**
- Produces: `internal sealed class OverlayClient` with:
  - `static OverlayClient? TryBind(string clientId, string displayName, Func<string, Task<string?>> onEvent)`: finds the type `Jellyfin.Plugin.JellyTvOverlay.OverlayHost` across `AssemblyLoadContext.All` → `Assemblies`, checks `ProtocolVersion == 1`, builds delegates with `Delegate.CreateDelegate`, calls `Register`. Returns null when it is missing.
  - `bool Show(string deviceId, string layoutJson)`, `void Close(string deviceId, string layoutId)`, `bool HasApp(string deviceId)`.
  - `static OverlayClient? Current` and `static void EnsureBound(...)`: retried lazily (the host may load after the client).
- Clients include it via `<Compile Include="..\shared\OverlayClient.cs" Link="OverlayClient.cs" />`. Its namespace is the including project's root namespace (`$(RootNamespace)` is not usable in C#, so put it in the fixed namespace `Xjellys.Overlay`; it is `internal`, so there are no clashes across assemblies).

- [ ] **Step 1: Write failing test** `TryBind_FindsHost_RoundTripShow`: the test assembly references the host, so `TryBind` → non-null; `Show` returns what `OverlayHost.Show` returns (dispatcher started in the test). Also `TryBind_ProtocolMismatch_Null`, using a fake type through the `typeLocator` seam `TryBind(..., Func<Type?>? typeLocator = null)`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `JellyTvOverlay: shared reflection client for plugins`.

## Milestone 2: App

### Task 7: App scaffold, setup flow port, JVM test harness

**Files:**
- Create: `apps/JellyTvOverlay/{AndroidManifest.xml,build.sh,test.sh,RELEASE_NOTES.md,res/…}`
- Create: `src/com/xjellys/tvoverlay/{MainActivity,Prefs,Api,BootReceiver}.java`, ported from `apps/JellyXR.TV/src/com/xjellys/jellyxr/tv/`
- Create: `test/com/xjellys/tvoverlay/SmokeTest.java`

**Interfaces:**
- Produces: `Api` keeps the existing auth / discovery / Quick Connect / `sessions()` / playstate calls. It drops all plugin calls (`heartbeat`, `jellycapHeartbeat`, `recapOverlay`, `recapHold`, `subSync*`, `setSubtitle`) and adds:
  - `JSONObject poll(String deviceId, long since) throws Exception` (read timeout 35 s)
  - `void event(JSONObject body) throws Exception`
  - `JSONObject heartbeat(String deviceId) throws Exception`
  - `void playstate(String sessionId, String command) throws Exception` (`Pause`/`Unpause`/`PlayPause`/`Seek`)
- `MainActivity` keeps sign-in, overlay permission, **Test panel** (now shows a bundled `res/raw/test_layout.json` through the renderer), and **Watch** picker. All text is renamed to JellyTvOverlay. The package is `com.xjellys.tvoverlay`, the APK is `build/jellytvoverlay.apk`.
- Secure connections:
  - `res/xml/network_security_config.xml`: base config with trust anchors `system` + `user` (no custom TrustManager anywhere) and `cleartextTrafficPermitted="true"`, wired in the manifest with `android:networkSecurityConfig`. The XML can't allow cleartext by IP range, and LAN addresses aren't known ahead, so the HTTP rule is enforced in code by the gate below. That gate is the real control.
  - Pure class `src/com/xjellys/tvoverlay/model/ServerUrl.java`:
    - `static String normalize(String input)`: bare host → `https://host` (port 8920 if none given). On a failed probe, `MainActivity` retries `http://host:8096` only if `isPrivate(host)`.
    - `static boolean isPrivate(String host)`: literal IPs and `localhost`/`*.local`. No DNS resolution, so a public hostname is never private.
    - `static boolean allowed(String url)`: https always; http only if `isPrivate`.
  - `Api.open` throws `IOException("Plain HTTP is only allowed on the local network")` when `!ServerUrl.allowed(base)`.
  - `MainActivity` shows "Not secured (local network)" under the server name for http.
  - The token is sent only in the `Authorization: MediaBrowser … Token="…"` header. Remove any log lines that print URLs with the token.
- App logo from `art/logo-source.jpg` (512×286 JPEG): a small `art/make_icons.py` (Pillow) writes:
  - `res/drawable-xhdpi/banner.png` 320×180: cover-resize the whole image to 16:9.
  - `res/mipmap-{mdpi,hdpi,xhdpi,xxhdpi}/ic_launcher.png` 48/72/96/144 px: a square centre crop of 286×286 on the triangle, then resize.
  - The generated PNGs are committed. The script is run by hand, not by `build.sh`.
- `test.sh`: downloads `junit-platform-console-standalone-1.11.3.jar` and `json-20240303.jar` from Maven Central into `build/testlib` if missing, compiles `src/**/model` + `logic` classes and `test/**`, and runs the console launcher. Model and logic classes must not import `android.*`.

- [ ] **Step 1: Write failing `test/com/xjellys/tvoverlay/model/ServerUrlTest.java`:**
  - `bareHost_https8920`: `"jf.example.com"` → `"https://jf.example.com:8920"`
  - `explicitHttp_kept`
  - `isPrivate`: true for `192.168.1.5`, `10.0.0.2`, `172.16.4.1`, `169.254.1.1`, `localhost`, `nas.local`, `fd00::1`; false for `172.32.0.1`, `8.8.8.8`, `jf.example.com`
  - `allowed`: `http://8.8.8.8:8096` false, `http://192.168.1.5:8096` true, `https://jf.example.com` true
- [ ] **Step 2: Port the files**, then rename package and strings, add `ServerUrl`, the security config and the `Api.open` gate, and generate the icons with `python apps/JellyTvOverlay/art/make_icons.py`.
- [ ] **Step 3: Run** `bash apps/JellyTvOverlay/build.sh` → `built build/jellytvoverlay.apk`.
- [ ] **Step 4: Run** `bash apps/JellyTvOverlay/test.sh` → `ServerUrlTest` and `SmokeTest` pass.
- [ ] **Step 5: Manual check:** `adb install`. The new logo shows in the TV launcher row. Sign in over HTTPS (and over LAN http, which shows "Not secured (local network)") with Quick Connect, allow the overlay, and see the Watch picker list the Jellyfin player. A public `http://` address is refused with the message above.
- [ ] **Step 6: Commit** `JellyTvOverlay app: scaffold, setup flow and JVM test harness`.

### Task 8: Layout model and parser

**Files:**
- Create: `src/com/xjellys/tvoverlay/model/{Layout,Node,KeyBinding}.java`
- Test: `test/com/xjellys/tvoverlay/model/LayoutTest.java`

**Interfaces:**
- Produces:
  - `final class Layout { String id, owner, focus, parent; int ttlMs; float dim; List<String> closeOn; Map<String,KeyBinding> keys; List<Layout> preload; Node root; static Layout parse(JSONObject o) throws JSONException; boolean interactive(); boolean passthrough(); }`
  - `final class Node { String type, id, action; float x,y,w,h /*NaN = flow*/; float pad, gap, radius; Integer bg; boolean focusable, enabled; JSONObject raw; List<Node> children; String text(); String dir(); … }`. Typed getters read `raw` with spec defaults; an unknown `type` → `Node` with `type` kept, rendered as nothing.
  - `final class KeyBinding { String op, action, target; double by; }`

- [ ] **Step 1: Write failing tests:** `parse_defaults` (focus passive, dim 0, closeOn stop+itemStart, text size 0.03, dir auto); `parse_unknownType_kept_noThrow`; `parse_unknownField_ignored`; `parse_keys_step`; `parse_preload_nested`; `button_disabled_notFocusable`; `bg_argb` (`#CC000000` → 0xCC000000).
- [ ] **Step 2: Run** `test.sh` → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `JellyTvOverlay app: layout v1 model and parser`.

### Task 9: Local logic: focus, value stepper, pages, sequence

**Files:**
- Create: `src/com/xjellys/tvoverlay/logic/{FocusModel,ValueState,Pager,Sequencer}.java`
- Test: `test/com/xjellys/tvoverlay/logic/*Test.java`

**Interfaces:**
- Consumes: `Layout`, `Node`, `KeyBinding` (Task 8).
- Produces:
  - `final class FocusModel { FocusModel(Layout l, Map<String, float[]> rects /* id -> l,t,r,b screen px */); String focused(); /** returns what the key did */ Result onKey(String key); }` with `Result { kind: MOVED|STEPPED|ACTION|BACK|PASSTHROUGH|NONE; String action, elementId; }`.
    - Order: a key binding wins. Otherwise arrows move to the nearest focusable in that direction (centre distance, overlap preferred). If there is none and the focused node is a `scroll` text → `NONE` (the renderer scrolls). `ok` on a node with an `action` → `ACTION`. `back` → `BACK`. In `interactive+passthrough`, `play` is always `PASSTHROUGH`, and so are `left`/`right` when unbound and no move is possible.
    - Initial focus: the first focusable in document order.
  - `final class ValueState { ValueState(Layout l); double get(String id); boolean step(String id, double by) /* clamps min/max */; String display(String id); JSONObject snapshot(); }`. Display: `value/scale`, `decimals`, `signed` adds `+`, then `suffix`.
  - `final class Pager { Pager(int count, int intervalMs); int page(); /** returns true if the page changed */ boolean tick(long nowMs); void next(); void prev(); }`
  - `final class Sequencer { Sequencer(int[] durationsMs); int index(long elapsedMs) /* -1 = finished */; }`

- [ ] **Step 1: Write failing tests:**
  - `FocusModelTest`: `down_movesToNearestBelow`, `binding_overridesMove`, `ok_onButton_action`, `disabledButton_skipped`, `passthrough_play`, `back_returnsBack`, `rtlRow_rightMovesToVisualRight` (rects decide, not document order).
  - `ValueStateTest`: SubSync values (`scale:1000, decimals:2, suffix:" s", signed:true, min:-600000, max:600000`): `step(+250)` from 0 → display `"+0.25 s"`; `step(-1000)` from 0 → `"-1.00 s"`; clamps at max.
  - `PagerTest`: 8000 ms interval, 3 pages, tick at 8001 → page 1; at 24001 → page 0.
  - `SequencerTest`: durations [3000, 5000]: 0 → 0, 3500 → 1, 8000 → -1.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `JellyTvOverlay app: local focus, stepper, pages and sequence logic`.

### Task 10: Renderer, overlay windows, images, icons

**Files:**
- Create: `src/com/xjellys/tvoverlay/render/{Renderer,OverlayWindow,ImageCache,Icons}.java`, `res/drawable/ic_*.xml` (vector icons for the bundled set), `res/raw/test_layout.json`

**Interfaces:**
- Consumes: `Layout`, `Node`, `FocusModel`, `ValueState`, `Pager`, `Sequencer`.
- Produces:
  - `final class OverlayWindow { OverlayWindow(Context c, ImageCache images, Listener l); void show(Layout l); void close(); boolean interactive(); }` with `interface Listener { void onAction(Layout l, String action, String elementId, JSONObject values); void onClosed(Layout l, String reason); void onPassthrough(String key); }`
  - `final class Renderer { static View build(Context c, Node n, int screenW, int screenH, ImageCache images, ValueState values); }`
  - `final class ImageCache { ImageCache(Api api, int maxBytes /* 24 MB */); void preload(String src); void into(ImageView v, String src); }`. A `src` starting with `/` → server origin plus token header. An absolute URL with the same origin as the server → token. Another origin → only `https`, no token. Anything else (`http` to another host, `file:`, `content:`, `javascript:`) → ignored. The pure decision lives in `model/ImageSource.java` `static Kind classify(String src, String serverBase)` → `SERVER | EXTERNAL | REJECT`, tested in `ImageSourceTest` (`relative_server`, `sameOrigin_server`, `otherHttps_external`, `otherHttp_reject`, `fileScheme_reject`).
- Window flags: `TYPE_APPLICATION_OVERLAY`. Passive layouts get `FLAG_NOT_FOCUSABLE | FLAG_NOT_TOUCHABLE`. Interactive layouts are focusable and handle `dispatchKeyEvent` through `FocusModel`; focus is shown as a 2 dp `#4AA3FF` outline plus a `#224AA3FF` fill (as in the mockup). `dim` = a full-screen `#000000` view with alpha `dim`. A text node with `dir:auto` uses `TextDirectionHeuristics.FIRSTSTRONG_LTR` and `View.LAYOUT_DIRECTION_LOCALE` → per node from the content (as `CastPanel` does for names today). One window per shown layout. Only one interactive window exists (enforced by the host; the app trusts it).
- Pages and sequences re-render only their container. A sequence end → `onAction(l, finishedAction, null, values)`, then `onClosed(l, "finished")`. TTL → `onClosed(l, "ttl")`. BACK → `onClosed(l, "back")` (the host reopens the parent if any; the app only closes).

- [ ] **Step 1: Implement**, following the ported `CastPanel` patterns for window params and RTL.
- [ ] **Step 2: Build** with `build.sh` → APK built.
- [ ] **Step 3: Manual check on a TV box:** Test panel → `test_layout.json` (an interactive copy of actor detail layout B with Hebrew sample text) renders. ▲▼◀▶ move focus with no visible delay, BACK closes, and the Jellyfin player gets the remote again right after.
- [ ] **Step 4: Commit** `JellyTvOverlay app: renderer and overlay windows`.

### Task 11: Overlay service: follow session, long-poll, events, passthrough, heartbeat

**Files:**
- Create: `src/com/xjellys/tvoverlay/OverlayService.java` (replaces the ported `WatchService`)
- Modify: `AndroidManifest.xml`, `BootReceiver.java`, `MainActivity.java`

**Interfaces:**
- Consumes: `Api.poll/event/heartbeat/playstate`, `OverlayWindow`, `Layout.parse`.
- Behaviour:
  - Foreground service, started on boot.
  - Picks the player session with the existing `pick()` logic (polled every 5 s; only used to know `deviceId` and `sessionId` of the player).
  - Heartbeat every 30 s with the player's deviceId.
  - Long-poll loop on its own thread: `since` starts at 0. Each op is applied on the main thread: `show` → cache `preload` layouts and images, and show or replace the window by id. A `show` for an id that is already in the preload cache uses the cached `Layout` so images are already decoded. `close` → close the window. Network error → back off 2 s, keep `since`. HTTP 401 → sign-out flow as today.
  - `onAction` / `onClosed` → POST `Event` (on a background thread; the UI never waits).
  - `onPassthrough("play")` → `api.playstate(sessionId, "PlayPause")`; `left`/`right` → `Seek` ±10 s from the last known position.
  - Status line in `MainActivity` like JellyXR TV.

- [ ] **Step 1: Implement.**
- [ ] **Step 2: Build** → APK built.
- [ ] **Step 3: Manual end-to-end check** with a throwaway test client (a debug-only `POST JellyTvOverlay/Debug/Show?deviceId=` endpoint, enabled only when `PluginConfiguration.DebugEndpoints` is true, default false; it stays, documented as a debug aid): show a passive layout → it appears within 1 s and the remote still drives the player. Show an interactive layout → OK sends an `action` event (visible in the plugin log). Kill the app and restart it → the snapshot shows the same layout again.
- [ ] **Step 4: Commit** `JellyTvOverlay app: overlay service with long-poll delivery and events`.

## Milestone 3: Clients

### Task 12: JellyXR cast panel as a host client

**Files:**
- Create: `plugins/Jellyfin.Plugin.JellyXR/Overlay/{CastLayoutBuilder.cs,OverlayClientHost.cs}`
- Modify: `Jellyfin.Plugin.JellyXR.csproj` (link `..\shared\OverlayClient.cs`), `CardService.cs` (add `Guid? PersonId` and `string? ImageUrl` to `Card`), `PauseCoordinator.cs`, `Configuration/PluginConfiguration.cs` (+`bool TvPanelInteractive = false`), `Configuration/configPage.html`
- Create: `tests/Jellyfin.Plugin.JellyXR.Tests/{csproj,CastLayoutBuilderTests.cs}`

**Interfaces:**
- Consumes: `OverlayClient` (Task 6), event shapes (Shared contract).
- Produces:
  - `public static class CastLayoutBuilder { public static JsonObject Build(CardSet set, bool interactive, int pageMs = 8000); }`
    - Layout id `cast`, `closeOn:["resume","stop","itemStart"]`.
    - Right-hand panel like today: `x 0.62 … w 0.36`.
    - A `pages` container: on-screen people first, the same pagination as `CastPanel.paginate`. Each person is a `row` with a round image (`/Items/{personId}/Images/Primary?maxHeight=128`), name (bold) and character. In interactive mode the rows are `focusable` with `action:"actor:<personId>"`.
  - `OverlayClientHost : IHostedService` binds `OverlayClient` with id `JellyXR`, display name `Cast`. Triggers:
    - `pause` (cfg `TvOverlay` on, the device has the app, and the client filter matches): if `TvPanelInteractive` → return offer `{"id":"cast","label":"Cast"}`; else build cards and `Show` passive.
    - `doublePause` → offer `{"id":"cast","label":"Cast"}`.
    - `chosen` → `Show` interactive.
  - `PauseCoordinator`: skips toasts when `OverlayClient.Current?.HasApp(deviceId) == true` (in addition to the old `CompanionRegistry` check, which stays for JellyXR TV this release).

- [ ] **Step 1: Write failing tests:** `Build_Passive_NoFocusable`; `Build_Interactive_RowsFocusableWithActorAction`; `Build_OnScreenFirst`; `Build_ManyPeople_Paginates` (same page size as `CastPanel`); `Build_TextDirAuto`.
- [ ] **Step 2: Run** `dotnet test tests/Jellyfin.Plugin.JellyXR.Tests` → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Manual check:** pause on the TV → passive panel within about 1 s, pages every 8 s, and the remote still drives the player. Without the app (stop the service) → toasts as before.
- [ ] **Step 6: Commit** `JellyXR: cast panel through JellyTvOverlay`.

### Task 13: Actor detail (JellyXR)

**Files:**
- Create: `plugins/Jellyfin.Plugin.JellyXR/Overlay/{ActorDetailBuilder.cs,PersonLibrary.cs}`
- Modify: `Overlay/OverlayClientHost.cs`
- Test: `tests/Jellyfin.Plugin.JellyXR.Tests/{ActorDetailBuilderTests.cs,PersonLibraryTests.cs}`

**Interfaces:**
- Consumes: `CastLayoutBuilder` (ids), `OverlayClient`.
- Produces:
  - `public sealed record PersonInfo(Guid Id, string Name, string? Bio, DateTime? Born, string? BirthPlace)`
  - `public sealed record LibraryTitle(Guid Id, string Name, int? Year, string Kind /*Movie|Series*/, long? RunTimeTicks, int? SeasonCount, string? Overview, DateTime? Premiere)`
  - `public static class PersonLibrary`:
    - `public static IReadOnlyList<LibraryTitle> Rollup(IEnumerable<LibraryTitle> movies, IEnumerable<(Guid SeriesId, LibraryTitle Series)> episodeSeries, IEnumerable<LibraryTitle> series, Guid currentItemId, Guid? currentSeriesId)`: dedupes series, drops the current movie or current series, sorts by `Premiere` descending (nulls last).
    - `public static async Task<(PersonInfo, IReadOnlyList<LibraryTitle>)> LoadAsync(ILibraryManager lib, Guid personId, User user, Guid currentItemId, CancellationToken ct)`: queries with `InternalItemsQuery { PersonIds = [personId], IncludeItemTypes = [Movie, Series, Episode], Recursive = true, User = user }`, maps episodes to their series, and calls `Rollup`.
  - `public static class ActorDetailBuilder { public static JsonObject Build(PersonInfo p, IReadOnlyList<LibraryTitle> titles); }`. Layout B:
    - id `actor:<personId>`, `parent:"cast"`, `focus:interactive`, `dim:0.55`, `closeOn:["resume","stop","itemStart"]`, box `x0.06 y0.06 w0.88 h0.88`, `bg:#E0000000`.
    - Portrait image, name (`size 0.045`, bold), born line (`yyyy · place`, omitted if both are missing), bio `text` with `scroll:true, focusable:true, maxLines:8`.
    - Heading "In your library", then a horizontal `list` of posters (`/Items/{id}/Images/Primary?maxHeight=300`), each focusable with `action:"title:<id>"`.
    - Under the list, a `stack` of description `text`s, one per title, id `desc:<id>`. The app shows the one for the focused poster, so add to the format **`showFor`** (node field: element id; the node is visible only while that element has focus). Add `showFor` to `layout-v1.md`, `Node`, and `Renderer` in this task.
    - Each description: `Name (Year) · 1h 52m` or `· 3 seasons`, plus the first 160 characters of the overview, plus a `button` "Play" with `enabled:false`.
    - No titles → `text` "No other titles in your library" in place of the list. No bio → bio node omitted.
- Flow in `OverlayClientHost`: when the cast panel is shown **interactive**, load `PersonLibrary` for every person in parallel (cap 8 at a time), build each detail, and `Show` the cast layout with them in `preload`. `action actor:<id>` → `Show` the preloaded detail (same JSON; the app uses its cache). App `closed` reason `back` on `actor:*` → `Show` the cast layout again interactive.

- [ ] **Step 1: Write failing tests:**
  - `Rollup_EpisodesBecomeOneSeries`
  - `Rollup_ExcludesCurrentMovieAndCurrentSeries`
  - `Rollup_NewestFirst_NullsLast`
  - `Build_HasParentCastAndInteractive`
  - `Build_DescriptionPerTitle_ShowForPoster`
  - `Build_PlayDisabled`
  - `NoTitlesNoBio_RendersPlaceholder` (no `list` node, placeholder text present, no bio node, every `text` has `dir` `auto`, Hebrew name `"גל גדות"` kept unchanged)
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** (plus `showFor` in the app: `Node.showFor()`, and the renderer toggles visibility on focus change; add the app test `FocusModelTest.showFor_visibleOnlyWhenFocused`).
- [ ] **Step 4: Run** `dotnet test tests/Jellyfin.Plugin.JellyXR.Tests` and `bash apps/JellyTvOverlay/test.sh` → PASS.
- [ ] **Step 5: Manual check:** double-pause → (chooser if SubSync applies) → Cast → ▼ to an actor, OK → detail appears with no visible wait (≤ 150 ms). ◀▶ moves along the posters and the description follows, ▲ reaches the bio and ▲▼ scrolls it. BACK → cast panel, BACK → closed, and the player has the remote.
- [ ] **Step 6: Commit** `JellyXR: actor detail with bio and library titles`.

### Task 14: Jellycap recap cards as a host client

**Files:**
- Create: `plugins/Jellyfin.Plugin.Jellycap/Overlay/{RecapLayoutBuilder.cs,RecapOverlayDriver.cs,OverlayClientHost.cs}`
- Modify: `Jellyfin.Plugin.Jellycap.csproj` (link the shared client), the places that check the JellyXR TV companion (also use `OverlayClient.Current?.HasApp`), `OverlayController.cs` (unchanged endpoints; mark `[Obsolete]` in comments only)
- Test: `tests/Jellyfin.Plugin.Jellycap.Tests/{csproj,RecapLayoutBuilderTests.cs,RecapOverlayDriverTests.cs}`

**Interfaces:**
- Consumes: the existing overlay recap store (`_overlays.Get(deviceId,itemId)` / `OverlayRecap` with `Mode`, `Cards`, `ResumeTicks`, `SessionId`, `TotalSeconds`), and the `Command` sequence of `ReleaseAsync`.
- Produces:
  - `public static class RecapLayoutBuilder { public static JsonObject Build(IReadOnlyList<RecapCard> cards, OverlayMode mode); }`:
    - Pause mode → one `sequence` (each card `durationMs` = card seconds × 1000), `finishedAction:"recapDone"`, `closeOn:["stop","itemStart"]` (no `resume`; the driver handles resume).
    - Timed mode → `closeOn:["stop","itemStart","seek"]`. The app has no clock tied to playback position, so the driver re-shows one card layout per card at its `At` time. Lay out the card text like the current `RecapCard`.
  - `public sealed class RecapOverlayDriver(Func<string, string, Task> playstate /* sessionId, command */, TimeProvider clock)`, a pure state machine:
    - `OnReady(recap)`: in pause mode → send `Pause`, then `Show`.
    - `OnAction("recapDone")` → close, `Seek` to `ResumeTicks`, `Unpause`.
    - `OnTrigger(resume)` while held → close, no seek (the user pressed play).
    - Fallback timer `TotalSeconds + 30` → as `recapDone`.
    - Timed mode: on `itemStart`, schedule a `Show` and `Close` per card at `At` / `At+Seconds` offsets from playback start; re-sync on `seek` by dropping the schedule and continuing from the trigger position.

- [ ] **Step 1: Write failing tests:**
  - `PauseMode_Ready_PausesThenShows`
  - `RecapDone_SeeksToResumeThenUnpauses`
  - `ResumeDuringCards_ClosesWithoutSeek`
  - `NoReleaseFromApp_FallbackResumesAfterTotalPlus30`
  - `TimedMode_ShowsCardAtItsTime`
  - `Builder_SequenceDurations`
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Manual check:** episode with Text recap → the episode pauses, the cards show in order, then it seeks back and plays. Play pressed mid-recap → the cards go and playback continues. `Both` → cards over their clips.
- [ ] **Step 6: Commit** `Jellycap: recap text cards through JellyTvOverlay`.

### Task 15: JellySubSync box and server-side apply sequence

**Files:**
- Create: `plugins/Jellyfin.Plugin.JellySubSync/Overlay/{SyncLayoutBuilder.cs,PendingSubtitleSwitch.cs,OverlayClientHost.cs}`
- Modify: `Jellyfin.Plugin.JellySubSync.csproj` (link the shared client)
- Test: `tests/Jellyfin.Plugin.JellySubSync.Tests/{csproj,SyncLayoutBuilderTests.cs,PendingSubtitleSwitchTests.cs}`

**Interfaces:**
- Consumes: `SubSyncService.GetState(Video, int)`, `SubSyncService.ApplyAsync(Video, int, long, CancellationToken) -> int`, `SyncScopeFilter`, `ISessionManager`.
- Produces:
  - `public static class SyncLayoutBuilder`:
    - `public static JsonObject Box(string trackLabel, long offsetMs, long maxOffsetMs)`:
      - id `box`, `focus:interactive`, `ttlMs:15000` (the app restarts the TTL on every key press; add the layout field **`ttlResetOnKey`** (bool) to the format, the model and the renderer in this task), `closeOn:["resume","stop","itemStart"]`.
      - A `value` node `offset` (`scale:1000, decimals:2, suffix:" s", signed:true, min:-max, max:max`).
      - `keys`: `left` step −250, `right` +250, `down` −1000, `up` +1000, `ok` and `play` → action `apply`.
      - Hint text as in today's `SyncBox`.
    - `public static JsonObject Message(string text, bool busy)`: same box, status text only, passive, `ttlMs` 0 when busy, 6000 otherwise.
  - `public sealed class PendingSubtitleSwitch(TimeProvider clock)`, a pure port of `WatchService.pendingSub` logic. Constants copied from JellyXR TV: `SUB_GIVE_UP_MS`, `SUB_SETTLE_MS`, `SUB_RETRY_MS`, `SUB_TRIES` (read the values from `WatchService.java`).
    - `void Start(Guid itemId, int streamIndex, bool canSet)`
    - `Decision OnSample(Guid? itemId, bool paused, int? currentSub)` → `None | SendSet(index) | Done | GiveUp(showHint)`.
  - `OverlayClientHost` (id `JellySubSync`, display name `Sync subtitles`):
    - `doublePause` → if `SyncScopeFilter` allows the item: offer `{"id":"sync","label":"Sync subtitles"}`; else null. An item that is allowed but has no subtitle track still offers, and the box then shows "Turn on a subtitle track in the player first." (as today).
    - `chosen` → `Show(Box(...))`.
    - `action apply` with `value.offset` → `Show(Message("Syncing subtitles…", busy))`, `ApplyAsync`, then `SendPlaystateCommand(Stop)`, wait until the session reports nothing playing (≤ the existing `STOP_WAIT_MS`, checking `ISessionManager.Sessions`), `Show(Message("Subtitles +x.xx s. Press play to resume.", false))`, and `PendingSubtitleSwitch.Start`.
    - On `PlaybackProgress` → `OnSample` → `SendGeneralCommand(SetSubtitleStreamIndex)` on `SendSet`; on `GiveUp` → `Show(Message("Pick the \"Synced\" track in the player's subtitle menu.", false))`.
    - **No `PlayNow`.**

- [ ] **Step 1: Write failing tests:**
  - `Box_KeysMatchToday` (left −250, right +250, down −1000, up +1000, ok/play apply)
  - `Box_ValueFormatting`
  - `Pending_WaitsForSettleThenSends`
  - `Pending_RetriesUntilTries_ThenGiveUp`
  - `Pending_DoneWhenCurrentMatches`
  - `Pending_OtherItem_Cancels`
  - `Pending_TimesOut_Quietly`
  - `Pending_CannotSet_GiveUpWithHint`
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS (plus `test.sh` for `ttlResetOnKey`).
- [ ] **Step 5: Manual check:** subtitle track on, double-pause → chooser (Cast / Sync subtitles) → Sync. The keys change the value instantly, OK → stop → "press play to resume" → resume from the item page → the player switches to *Synced* within a few seconds. Offset 0 → back to the original. If the switch is unreliable server-side, stop and report it (the spec's fallback is a generic app capability, which needs a design update).
- [ ] **Step 6: Commit** `JellySubSync: sync box and apply sequence through JellyTvOverlay`.

### Task 16: Release wiring, docs, retire JellyXR TV

**Files:**
- Modify: `README.md`, `plugins/*/meta.json` (changelogs), version bumps in the three client csproj files, `apps/JellyXR.TV/RELEASE_NOTES.md` (deprecation note)
- Create: `plugins/Jellyfin.Plugin.JellyTvOverlay/{JellyTvOverlay.png}` (optional image), `apps/JellyTvOverlay/RELEASE_NOTES.md` (1.0.0)

- [ ] **Step 1: README:**
  - Replace the JellyXR TV section with JellyTvOverlay (install, setup, overlay permission + adb line, Test panel, Watch).
  - Add a host plugin section and a "For plugin authors" paragraph linking `docs/jellytvoverlay/layout-v1.md` and `plugins/shared/OverlayClient.cs`.
  - Add "Uninstall JellyXR TV after installing JellyTvOverlay."
  - Update the JellyXR/Jellycap/SubSync sections where they mention the JellyXR TV app.
- [ ] **Step 2: Versions:** JellyTvOverlay plugin 1.0.0.0; JellyXR 1.7.0.0; Jellycap 1.3.0.0; JellySubSync 1.1.0.0; app 1.0.0. Changelogs say "works with JellyTvOverlay; JellyXR TV still supported this release".
- [ ] **Step 3: Verify the whole build:** `dotnet build` for each of the 4 plugins → 0 errors; `dotnet test tests/` (each test project) → all pass; `bash apps/JellyTvOverlay/build.sh` and `test.sh` → pass.
- [ ] **Step 4: Commit** `JellyTvOverlay 1.0: docs, versions, JellyXR TV deprecated`. Do not push tags; releasing is the user's call (`git tag Jellyfin.Plugin.JellyTvOverlay-v1.0.0.0`, `git tag app-JellyTvOverlay-v1.0.0`, plus the client plugin tags).
