# Codex Quota Bridge — V1 Design Specification

**Date:** 2026-09-22  
**Status:** Design approved in conversation; awaiting written-spec review  
**Target user:** Personal use first; architecture must not block small-scale sharing later  
**Primary devices:** Windows 11 PC, OPPO Find X8, HUAWEI WATCH FIT 2

## 1. Purpose

Build a small personal system that reads the current Codex/ChatGPT rate-limit state on a Windows 11 PC, presents it on an Android phone, and mirrors concise status/alert notifications to a HUAWEI WATCH FIT 2 through the normal Android → HUAWEI Health notification path.

V1 optimizes for local-LAN use and personal reliability. It must preserve clear extension points for future remote access and multiple paired devices, without implementing either feature yet.

### 1.1 Success criteria

V1 is successful when the user can:

1. Manually start a Windows tray application.
2. Authorize the Bridge with ChatGPT/Codex once.
3. Pair an OPPO Find X8 over the LAN using automatic discovery or QR fallback.
4. See current short-window and weekly Codex quota state on Android.
5. See the last 24 hours as a chart plus meaningful event list.
6. Choose independently between:
   - a persistent quota status notification,
   - low-quota alerts,
   - both,
   - neither.
7. Configure warning and critical thresholds independently for the short-window and weekly quota.
8. Receive critical repeat reminders only when enabled and data is fresh.
9. See concise quota notifications on WATCH FIT 2.
10. Keep viewing cached data/history when the Bridge is unavailable, with stale state clearly labeled.
11. Reconnect automatically after ordinary LAN/IP changes without re-pairing.
12. Refuse a changed Bridge cryptographic identity instead of silently trusting it.

## 2. Explicit V1 non-goals

V1 does **not** include:

- a native HUAWEI WATCH FIT 2 application;
- Wear OS support;
- iOS support;
- public-cloud synchronization;
- a public Internet server;
- multi-account Codex support;
- production-grade multi-device UI, despite multi-device-ready storage/credential design;
- Windows auto-start;
- Android boot auto-start of live sync;
- automatic application updates;
- a full Windows desktop dashboard;
- long-term analytics beyond a 24-hour visible history window;
- scraping the ChatGPT website;
- storing ChatGPT passwords or manually extracted browser cookies;
- Android directly authenticating to OpenAI/Codex;
- vendor-private OPPO/ColorOS background-service hacks;
- bypassing TLS/hostname/security failures;
- remote access in V1.

## 3. Platform assumptions and external constraints

These items are platform facts rather than application design choices and should be revalidated at implementation time if platform versions have materially changed.

### 3.1 Codex App Server

The Bridge uses Codex App Server as the quota/authentication source. Current official documentation exposes:

- `account/read`;
- `account/login/start` with ChatGPT-managed OAuth;
- `account/login/completed` and `account/updated` notifications;
- `account/rateLimits/read`;
- `account/rateLimits/updated` notifications;
- rate-limit fields including `usedPercent`, `windowDurationMins`, and `resetsAt`;
- optional multi-limit views such as `rateLimitsByLimitId`.

The design must not assume `primary` always means a specific quota period. The adapter identifies supported windows using explicit window metadata and fails safely if the schema cannot be interpreted.

Reference: https://developers.openai.com/zh-Hans/docs/app-server

### 3.2 Android foreground work

Live LAN synchronization uses a `connectedDevice` foreground service because Android documents that type for interaction with external devices requiring a network connection.

Do **not** use a `dataSync` foreground service for all-day live sync. Android 15+ imposes a 6-hour-per-24-hour background limit on `dataSync` foreground services for apps targeting the affected API levels.

References:

- https://developer.android.com/develop/background-work/services/fgs/service-types
- https://developer.android.com/develop/background-work/services/fgs/timeout

### 3.3 Android background periodic work

Low-power alert-only mode uses WorkManager periodic work. Periodic work has a minimum interval of 15 minutes and execution is inexact due to scheduler, Doze, battery, and constraints.

Reference: https://developer.android.com/reference/androidx/work/PeriodicWorkRequest

### 3.4 Future local-network permission

The networking layer must isolate LAN-permission handling behind a dedicated permission controller. Android 17 introduces `ACCESS_LOCAL_NETWORK` for apps targeting API 37+ that discover/connect to LAN devices.

Reference: https://developer.android.com/about/versions/17/behavior-changes-17

### 3.5 WATCH FIT 2 notification behavior

HUAWEI documentation confirms WATCH FIT 2 supports phone notifications, but exact behavior for updates to an existing Android ongoing notification is treated as a real-device compatibility question. V1 therefore includes a device-validation gate before adding any workaround.

Reference: https://consumer.huawei.com/jp/support/content/ja-jp15956241/

## 4. System architecture

```text
Codex App Server
       │ JSON-RPC / managed child process
       ▼
Windows 11 CodexQuota Bridge
 ├─ Codex adapter
 ├─ current quota state
 ├─ 24h authoritative history
 ├─ REST API
 ├─ WebSocket hub
 ├─ pairing + TLS identity
 ├─ mDNS/DNS-SD discovery
 └─ tray UI
       │
       │ HTTPS + WSS over LAN
       ▼
Android CodexQuota App
 ├─ paired-Bridge client
 ├─ local cache
 ├─ live/background sync controller
 ├─ per-phone alert engine
 ├─ Android notifications
 └─ Compose UI
       │
       │ normal Android notification mirroring
       ▼
HUAWEI Health → WATCH FIT 2
```

### 4.1 Single authoritative business-data direction

```text
Codex → Windows Bridge → Android → WATCH FIT 2
```

The Windows Bridge is authoritative for quota state and raw 24-hour history.

Android settings are local to the phone and do not flow back into the Bridge except for ordinary connection/authentication activity. Alert thresholds are intentionally phone-local so a future second phone may use different alert preferences.

## 5. Technology choices

### 5.1 Windows

- C#
- .NET 8
- ASP.NET Core/Kestrel for local HTTPS/WSS API
- lightweight Windows tray UI; UI framework may be WPF or WinUI, but domain/networking logic must not depend on it
- SQLite for quota history/events/device metadata
- structured logging with mandatory redaction

### 5.2 Android

- Kotlin
- Jetpack Compose
- Room for cached snapshot/history/events/alert state
- DataStore for ordinary settings
- Android Keystore-backed protection for credentials/paired identity material
- WorkManager for low-power periodic mode
- a `connectedDevice` foreground service for live mode

### 5.3 WATCH FIT 2

No custom app in V1. It receives concise Android notifications through HUAWEI Health.

## 6. Windows component boundaries

```text
CodexQuota.Bridge
├─ Core
│  ├─ QuotaSnapshot
│  ├─ RateLimitWindow
│  ├─ HistoryPoint
│  └─ DomainEvents
├─ Codex
│  ├─ CodexProcessManager
│  ├─ CodexRpcClient
│  └─ RateLimitAdapter
├─ Storage
│  ├─ SQLiteHistoryRepository
│  └─ BridgeSettingsRepository
├─ Networking
│  ├─ RestApi
│  ├─ WebSocketHub
│  ├─ PairingService
│  ├─ MdnsDiscovery
│  └─ CertificateManager
└─ Desktop
   ├─ TrayIcon
   ├─ StatusWindow
   ├─ PairingQrWindow
   └─ LogViewer
```

Rules:

- UI never talks directly to Codex App Server.
- API handlers never directly manipulate SQLite details.
- `RateLimitAdapter` is the only layer aware of Codex quota schema details.
- Codex code does not know Android exists.
- current live state must remain usable if historical persistence fails.

## 7. Android component boundaries

```text
app
├─ domain
│  ├─ QuotaState
│  ├─ BridgeStatus
│  ├─ AlertRule
│  └─ AlertState
├─ data
│  ├─ BridgeApi
│  ├─ BridgeWebSocket
│  ├─ LocalDatabase
│  └─ SettingsStore
├─ sync
│  ├─ LiveBridgeService
│  ├─ BackgroundSyncWorker
│  ├─ SyncModeController
│  ├─ ConnectionManager
│  └─ LocalNetworkPermissionController
├─ alerts
│  ├─ ThresholdEvaluator
│  ├─ RepeatReminderScheduler
│  └─ QuotaNotificationManager
└─ ui
   ├─ Dashboard
   ├─ History
   ├─ Alerts
   ├─ Connection
   └─ Settings
```

Rules:

- Compose/ViewModels do not own sockets, WorkManager, or NotificationManager.
- a single state flow represents connection state.
- alert evaluation is a deterministic domain operation, separate from notification delivery.
- live and periodic sync must be mutually exclusive.

## 8. Canonical data model

### 8.1 Quota snapshot

The public model is normalized by the Bridge and uses remaining percentage as the primary UI semantic.

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-09-22T13:30:00Z",
  "source": "codex_app_server",
  "status": "online",
  "lastSuccessfulSyncAt": "2026-09-22T13:30:00Z",
  "windows": {
    "shortWindow": {
      "usedPercent": 28.0,
      "remainingPercent": 72.0,
      "windowMinutes": 300,
      "resetsAt": "2026-09-22T15:42:00Z"
    },
    "weekly": {
      "usedPercent": 46.0,
      "remainingPercent": 54.0,
      "windowMinutes": 10080,
      "resetsAt": "2026-09-28T00:00:00Z"
    }
  }
}
```

Notes:

- storage and protocol timestamps are UTC;
- UI converts to local time;
- `remainingPercent` is derived by the Bridge from trusted source values when needed;
- missing/unsupported data must never be silently converted to `0%` or `100%`.

### 8.2 Data status

Supported high-level states:

- `online`
- `stale`
- `unavailable`
- `auth_required`
- `source_error`
- `source_schema_unsupported`

Bridge reachability and Codex source availability are distinct states. An online Bridge can still report `auth_required`.

### 8.3 History point

```json
{
  "timestamp": "2026-09-22T12:15:00Z",
  "shortWindowRemainingPercent": 81.0,
  "weeklyRemainingPercent": 60.0
}
```

Persistence rule: insert when any quota changes, when a reset occurs, on first sample, or when at least five minutes have elapsed since the previous persisted sample.

### 8.4 Event model

User-meaningful event types include:

- `quota_changed`
- `window_reset`
- `bridge_started`
- `bridge_stopped`
- `codex_connected`
- `codex_disconnected`
- `auth_required`
- `source_error`

Technical ping, SQLite cleanup, and routine HTTP success events do not appear in user history.

## 9. REST API v1

Base path:

```text
/api/v1/
```

Required endpoints:

- `GET /api/v1/health`
- `GET /api/v1/info`
- `GET /api/v1/quota`
- `GET /api/v1/history?hours=24`
- `GET /api/v1/events?hours=24`
- pairing endpoints under `/api/v1/pairing/`

### 9.1 API version rules

Keep these independent:

- application version (`1.4.2` style);
- API path version (`v1`);
- JSON schema version (`1`).

Within `v1`:

- adding optional fields is allowed;
- clients ignore unknown fields;
- deleting required fields is forbidden;
- changing field type or meaning is forbidden;
- breaking change requires `/api/v2/`.

### 9.2 Error envelope

```json
{
  "error": {
    "code": "CODEX_AUTH_REQUIRED",
    "message": "Codex authentication is required.",
    "retryable": false
  }
}
```

Stable machine-readable codes include at least:

- `PAIRING_INVALID`
- `PAIRING_EXPIRED`
- `DEVICE_UNAUTHORIZED`
- `CODEX_AUTH_REQUIRED`
- `CODEX_UNAVAILABLE`
- `RATE_LIMIT_DATA_UNAVAILABLE`
- `SOURCE_SCHEMA_UNSUPPORTED`
- `SECURITY_IDENTITY_MISMATCH`
- `API_VERSION_UNSUPPORTED`
- `BRIDGE_INTERNAL_ERROR`

Android must branch on `code`, never by parsing English text.

## 10. WebSocket v1

Endpoint:

```text
wss://<bridge-endpoint>/api/v1/ws
```

Server sends a hello frame with API and Bridge version, then meaningful events.

Quota updates carry the **complete normalized snapshot**, not just a delta.

Each state update carries a monotonically increasing `sequence` value. If Android sees a gap, it performs `GET /api/v1/quota` and replaces local current state instead of trying to replay missing WebSocket messages.

Heartbeat interval should be on the order of 30–60 seconds; it is only for dead-connection detection, not quota polling.

## 11. Quota-source adapter behavior

### 11.1 App Server ownership

`CodexQuotaBridge.exe` owns a managed Codex App Server child process.

The Bridge must not:

- attach to the internal process tree of the Codex desktop application;
- depend on the Codex desktop application being open;
- scrape desktop-app files for unstable internal APIs.

Distribution/packaging of the exact supported App Server binary is an implementation-plan concern. The runtime dependency must be explicit and versioned.

### 11.2 Initialization and login

Startup flow:

```text
STARTING
→ CODEX_INITIALIZING
→ CHECKING_AUTH
→ AUTH_REQUIRED or SYNCING
→ READY
```

The Bridge performs App Server `initialize`, checks account state, and when needed starts ChatGPT-managed OAuth through `account/login/start`.

The user completes authentication in the system browser. The Bridge never receives the ChatGPT password.

### 11.3 Quota refresh

- initial state: `account/rateLimits/read`;
- primary updates: `account/rateLimits/updated`;
- watchdog reconciliation: low-frequency `account/rateLimits/read`, target interval five minutes;
- manual `Refresh Now`: one immediate `account/rateLimits/read` only.

Client API requests never call Codex directly; `/quota` reads the Bridge's current in-memory snapshot.

### 11.4 Window identification

Do not hard-code `primary == shortWindow` or `secondary == weekly`.

The adapter:

1. examines available rate-limit buckets;
2. uses explicit metadata such as `windowDurationMins` and limit identifiers/names where appropriate;
3. maps only windows it can identify unambiguously;
4. returns `SOURCE_SCHEMA_UNSUPPORTED` when it cannot safely identify the required windows.

The user must see the last trusted state plus an error indicator rather than guessed quota values.

## 12. Pairing and local discovery

### 12.1 Discovery

Bridge publishes a DNS-SD/mDNS service:

```text
_codexquota._tcp.local.
```

Discovery metadata may include:

- non-personal Bridge ID;
- API version;
- TLS capability;
- port.

It must not broadcast secrets, OpenAI account details, Windows username, tokens, or private keys.

Android prefers automatic discovery and falls back to scanning a QR code.

### 12.2 Bridge identity

First Bridge startup generates a stable random Bridge ID and long-lived Bridge cryptographic identity.

The identity is the stable trust anchor; IP address is only a location.

Changing DHCP address or network interface must not require re-pairing as long as the Bridge identity remains the same.

### 12.3 TLS model

Use HTTPS/WSS only.

The design uses a long-lived Bridge identity that signs/anchors replaceable local TLS server certificates. Android pins/verifies the paired Bridge identity rather than blindly trusting any self-signed certificate.

Requirements:

- no `TrustAllCertificates`;
- no `HostnameVerifier = true` equivalent;
- never send Bearer credentials before Bridge identity is verified;
- leaf certificate renewal/IP SAN changes must not change Bridge identity;
- a changed Bridge identity causes `SECURITY_ERROR` and requires explicit re-pairing.

Do not use ASP.NET development certificates as the production Bridge identity.

### 12.4 Pairing session

Pairing session:

- generated only on user action;
- expires after five minutes;
- one-time use;
- becomes invalid immediately after successful pairing.

QR payload contains connection metadata, one-time pairing ID, and Bridge identity fingerprint, but **no long-lived device token**.

Automatic discovery pairing still requires user confirmation; discovery alone never establishes trust.

### 12.5 Device credential

After successful pairing the Bridge issues a cryptographically random credential with at least 256 bits of entropy.

Android receives:

- `deviceId`;
- device credential.

Bridge stores only a hash of the high-entropy credential. Android protects the credential using Keystore-backed storage.

The database schema supports multiple device records, while the V1 UI permits one active phone.

## 13. LAN network exposure

Bridge defaults to a private LAN interface and does not intentionally expose the API on public-network profiles, VPN adapters, Hyper-V/Docker virtual adapters, or arbitrary Internet-facing interfaces.

Firewall configuration, if automated, must be scoped to:

- the Bridge executable;
- the selected TCP port;
- the Windows Private profile.

Never ask the user to disable Windows Firewall.

A future remote transport must plug in behind an endpoint-provider abstraction rather than changing business models:

```text
EndpointProvider
├─ LocalLan      (V1)
└─ RemoteTunnel  (future)
```

## 14. Windows persistence

SQLite database contains at least:

- `quota_samples`
- `quota_events`
- `paired_devices`
- `bridge_metadata`

All persisted timestamps are UTC.

The API exposes at most the most recent 24 hours. Storage may retain approximately 25 hours to provide boundary margin, with periodic cleanup.

If SQLite/history fails:

- current in-memory quota state continues;
- `/quota` continues;
- WebSocket current-state updates continue;
- history is marked unavailable;
- the Bridge does not crash solely because history persistence failed.

## 15. Windows runtime lifecycle

### 15.1 User interaction

V1 is manual-start only.

- launching the executable creates the tray app and starts its managed services;
- closing the small status window hides to tray;
- `Tray → Exit` performs a true orderly shutdown;
- Windows auto-start is not implemented.

### 15.2 App Server recovery

Unexpected App Server exit uses bounded exponential backoff, e.g. 1s, 2s, 5s, 10s, 30s.

If repeated crashes exceed a defined threshold (target: five within five minutes), transition to `CODEX_FAULTED`, stop automatic restart looping, and expose `Retry` and `View Logs`.

### 15.3 Bridge shutdown ordering

1. stop new pairing;
2. send `bridge.shutdown` to connected clients when possible;
3. stop mDNS advertisement;
4. stop HTTPS/WSS listeners;
5. flush persistence;
6. stop the managed Codex child process;
7. dispose resources;
8. exit.

## 16. Windows tray UI

Minimal tray menu:

```text
Codex Quota Bridge
5h / short      72%
Weekly          54%
● Codex Connected
● Phone Connected
----------------
Open Status
Pair Device
Refresh Now
----------------
Open Logs
Settings
----------------
Exit
```

Status window shows quota, reset times, Codex state, Android connection, and last update. It is not a full analytics dashboard.

Windows settings cover:

- display language;
- minimize-to-tray behavior;
- selected LAN interface/port/device name;
- Codex account/login/logout state;
- data/history clearing;
- paired device reset/revoke;
- security information.

## 17. Android run modes

Only three background modes exist:

### 17.1 `MODE_OFF`

Condition:

- status notification OFF;
- low-quota alerts OFF.

Behavior:

- no foreground service;
- no periodic WorkManager sync;
- opening the app may perform an immediate sync.

### 17.2 `MODE_BACKGROUND`

Condition:

- status notification OFF;
- low-quota alerts ON.

Behavior:

- no long-lived WebSocket;
- unique periodic WorkManager job, target interval 15 minutes;
- each run performs a short REST sync and alert evaluation;
- actual alert latency may exceed 15 minutes due to OS scheduling.

### 17.3 `MODE_LIVE`

Condition:

- status notification ON, regardless of low-quota alert setting.

Behavior:

- `LiveBridgeService` runs as a `connectedDevice` foreground service;
- one WebSocket connection to the Bridge;
- status notification is the required visible foreground-service notification;
- periodic worker is cancelled while live mode is active.

## 18. Android restart and OS behavior

V1 does not start live sync from `BOOT_COMPLETED`.

After a phone reboot:

- saved setting may still say live status is enabled;
- live service remains stopped until the user opens the app and chooses to resume;
- pairing and alert settings remain intact.

If the user/system explicitly stops the foreground service, the app does not fight that action with an infinite auto-restart loop.

No OPPO-private hidden activities or undocumented vendor APIs are used to bypass power management.

## 19. Android connection state machine

Canonical states:

```text
UNPAIRED
DISCOVERING
CONNECTING
CONNECTED
RECONNECTING
OFFLINE_CACHED
AUTH_REQUIRED
REPAIR_REQUIRED
SECURITY_ERROR
```

Important transitions:

- TLS identity mismatch → `SECURITY_ERROR`, no credential sent;
- device credential rejected → `REPAIR_REQUIRED`;
- LAN disappears (e.g. Wi-Fi → cellular in V1) → `OFFLINE_CACHED`;
- network change cancels pending retry delay and triggers immediate endpoint rediscovery;
- WebSocket sequence gap triggers REST snapshot reconciliation.

Reconnection backoff target:

```text
1s → 2s → 5s → 10s → 30s → 60s max, with ±20% jitter
```

A stable successful connection resets the counter.

## 20. Android caching and offline display

Android caches:

- latest trusted snapshot;
- enough recent history/events to display the last synchronized 24-hour view;
- alert state;
- Bridge identity/endpoint metadata.

Offline behavior:

- keep last trusted values;
- label them stale and display last-update time;
- keep cached history view accessible;
- do not generate new threshold-crossing alerts from stale data;
- pause repeated critical alerts while the Bridge/source is stale or offline.

Never present cached numbers as real-time data.

## 21. Android UI

Bottom navigation:

- Dashboard
- History
- Settings

### 21.1 Dashboard

Primary semantic is **remaining percentage**.

Each quota card shows:

- remaining percentage;
- progress indicator;
- secondary used percentage if desired;
- reset time;
- freshness/connection state.

Status labels are textual as well as visual:

- Real-time
- Reconnecting
- Offline / cached
- Login required
- Security error

### 21.2 History

24-hour history consists of:

- separate short-window and weekly chart views rather than two default-overlaid lines;
- data gaps rendered as gaps, not interpolated continuous usage;
- reset points;
- meaningful event timeline.

### 21.3 Settings

Sections:

- Alerts
- Bridge connection
- Watch display
- Data/cache
- Logs
- About

## 22. Alert settings

Status notification and low-quota alerts are independent switches.

For each quota window, user configures:

- warning threshold;
- critical threshold.

Defaults:

- warning: 20%;
- critical: 10%.

Rules:

- valid range: 1–99%;
- critical must be lower than warning;
- short-window and weekly values are independent.

Optional repeated reminder:

- applies to critical state only;
- default OFF;
- configurable interval, with initial UI choices such as 30/60/120 minutes.

## 23. Alert engine

The core evaluator is deterministic and side-effect free in concept:

```text
evaluate(previousSnapshot, currentSnapshot, rules, previousAlertState, now)
→ AlertActions
```

### 23.1 Warning

Trigger once when crossing from above warning to at/below warning.

If the first ever trusted snapshot is already below warning, issue one appropriate initial alert.

### 23.2 Critical

Trigger once when crossing to at/below critical.

If one update jumps across both warning and critical, emit only the critical user notification while updating internal state appropriately.

### 23.3 Rearming

- critical rearms after trusted data returns above critical;
- warning rearms after trusted data returns above warning;
- window reset clears alert state before evaluating the new window.

### 23.4 Critical repeat

Repeat only when all are true:

- user enabled repeat;
- current trusted value remains at/below critical;
- source is fresh/online;
- repeat interval has elapsed.

Use monotonic elapsed-time facilities for in-process interval measurements; persist sufficient UTC state for process restoration.

## 24. Android notifications

Two notification channels:

- `codex_status` — low importance, default quiet;
- `codex_alerts` — normal/high user-visible alerts, subject to system/user channel settings.

### 24.1 Persistent status

Compact text target:

```text
Codex
5h 72% | W 54%
5h reset 23:42
```

Chinese compact option may render:

```text
Codex
5小时 72% | 周 54%
23:42 重置
```

The same notification ID is updated in place on Android. Ordinary quota changes update status only and do not create alert-channel notifications.

### 24.2 Warning/critical

Examples:

```text
⚠ Codex
5h 剩余 18%
```

```text
⚠ Codex
5h 仅剩 8%
```

Weekly alerts substitute the weekly label.

### 24.3 Offline status

Example:

```text
Codex
离线 | 5h 72% | W 54%
21:42 的数据
```

The stale label is mandatory.

### 24.4 Notification permission

On Android versions requiring runtime notification permission, enabling either notification feature requires the relevant permission flow. If permission/channel is disabled at OS level, the app must expose the actual disabled state instead of claiming notifications are healthy.

## 25. WATCH FIT 2 compatibility policy

V1 assumes Android notifications are mirrored through HUAWEI Health.

Priority of information on the watch:

1. short-window remaining percentage;
2. weekly remaining percentage;
3. short-window reset time;
4. weekly reset time.

A Settings action sends a test notification so the user can verify the end-to-end path.

### 25.1 Real-device gate

Before implementing any special refresh workaround, test whether updating the existing Android ongoing notification causes the WATCH FIT 2 to reflect new content.

If yes: keep the simple design.

If no: design a throttled, quiet refresh-event compatibility layer in a follow-up implementation decision. Do not pre-build this workaround without evidence.

## 26. Security requirements

V1 security acceptance requires all of the following:

- HTTPS/WSS only for Bridge API;
- unpaired devices cannot read quota/history;
- pairing sessions expire and are single-use;
- long-term device credential has ≥256-bit entropy;
- Bridge stores no plaintext long-term device credential;
- Android stores no plaintext long-term device credential;
- Bridge identity private material is protected by Windows-user secure storage and not plaintext config;
- OAuth password never passes through the Bridge;
- OAuth/access/refresh tokens are not logged;
- Authorization headers are not logged;
- private keys/pairing secrets are not logged;
- diagnostics exports are sanitized;
- identity mismatch stops before Bearer credential transmission;
- unknown Codex quota schema is not guessed;
- public networks are not exposed by default.

## 27. Logging and diagnostics

Structured log fields include timestamp, level, component, event ID, message, and safe properties.

A mandatory redaction layer processes log output before disk persistence.

Default logging is informational/warning/error, not verbose debug.

Suggested retention:

- 5 MB per file;
- up to 5 rotated files.

Future/small-sharing-ready diagnostic export may contain:

- sanitized logs;
- application/environment versions;
- sanitized configuration;

and must exclude credentials, private keys, OAuth tokens, cookies, and raw authorization headers.

## 28. Performance and power targets

### 28.1 Windows

- idle CPU should be near zero;
- no busy polling loops;
- typical memory target under roughly 150 MB unless profiling justifies otherwise;
- `/quota` reads in-memory current state and does not invoke Codex or SQLite;
- 24-hour history storage remains small.

### 28.2 Android

`MODE_OFF`:

- no recurring background network activity.

`MODE_BACKGROUND`:

- network only during scheduled Worker execution.

`MODE_LIVE`:

- one WebSocket connection;
- no 1-second polling;
- heartbeat roughly 30–60 seconds;
- ordinary status updates do not create repeated vibrating alerts.

Do not promise a fixed battery percentage; validate for abnormal wakeups, loops, or excessive network activity on the real Find X8.

## 29. Testing strategy

Five layers:

1. unit tests;
2. component tests;
3. protocol/contract tests;
4. integration/fault-injection tests;
5. real-device tests.

### 29.1 Windows priority tests

- RateLimitAdapter mapping and rejection cases;
- quota state-store atomic replacement;
- history write policy;
- pairing expiration/single-use;
- token validation/revocation;
- Bridge identity/certificate renewal;
- App Server crash/restart/backoff;
- SQLite/history failure isolation;
- log-secret redaction.

### 29.2 Android priority tests

Alert engine cases include:

- 25 → 19 triggers warning;
- 19 → 18 does not repeat warning;
- 19 → 9 triggers critical;
- 25 → 9 emits only critical notification;
- 9 → 100 reset and rearm;
- 9 → 12 rearms critical but not warning if warning is 20;
- 19 → 21 rearms warning;
- first trusted snapshot 18 triggers warning;
- first trusted snapshot 8 triggers critical only;
- stale snapshot triggers no new threshold alert;
- repeat critical only after interval and only while fresh.

Connection tests include:

- disconnect → reconnect;
- identity mismatch → terminal security error until user action;
- invalid credential → repair required;
- Wi-Fi → cellular → offline cached;
- LAN return → immediate rediscovery/reconnect;
- WebSocket sequence gap → REST reconciliation.

### 29.3 Contract fixtures

Maintain versioned JSON fixtures such as:

```text
contracts/
├─ quota-v1.json
├─ quota-stale-v1.json
├─ history-v1.json
├─ events-v1.json
├─ error-auth-required-v1.json
└─ ws-quota-updated-v1.json
```

Windows tests prove the server can produce contract-compliant payloads; Android tests prove it can parse them and ignore unknown optional fields.

Missing required fields must fail as protocol errors, not silently become zero values.

### 29.4 Fault injection

Inject at least:

- Codex child launch failure;
- initialize timeout;
- malformed source JSON;
- source schema change;
- login expiration;
- child crash loop;
- SQLite lock/write failure/corruption;
- WebSocket drop/duplicate/gap/out-of-order event;
- DHCP/IP change;
- mDNS unavailable;
- firewall-blocked port;
- TLS identity replacement;
- Android process death;
- phone reboot;
- notification/channel disablement.

## 30. Real-device validation matrix

Required physical chain:

```text
Windows 11 → OPPO Find X8 → HUAWEI Health → HUAWEI WATCH FIT 2
```

Must validate:

- initial pairing;
- auto discovery and QR fallback;
- reconnect after Bridge restart;
- reconnect after PC IP change;
- Wi-Fi → cellular and cellular → Wi-Fi;
- status notification;
- warning alert;
- critical alert;
- offline/stale rendering;
- compact and Chinese watch text;
- watch truncation/readability;
- whether ongoing notification content updates propagate to watch;
- whether status updates cause unwanted vibration.

## 31. Definition of Done by implementation stage

### Stage A — Windows quota core

Done only when:

- App Server starts/stops under Bridge ownership;
- initialization works;
- ChatGPT login works;
- real rate-limit data can be read;
- required quota windows are safely identified;
- current in-memory snapshot works;
- 24-hour SQLite history works;
- child-process crash recovery works;
- tray status and manual refresh work;
- secret-redaction tests pass.

### Stage B — LAN protocol and security

Done only when:

- HTTPS/WSS works;
- stable Bridge identity works;
- replaceable leaf certificate lifecycle works;
- REST v1 and WebSocket v1 contract tests pass;
- mDNS discovery works;
- QR pairing works;
- single-use/expiry logic works;
- device token authentication/revocation works;
- sequence-gap recovery works.

### Stage C — Android data/UI foundation

Done only when:

- secure Bridge pairing works;
- TLS identity verification works;
- current quota REST sync works;
- WebSocket live updates work;
- Room cache works;
- stale state works;
- Dashboard works;
- 24-hour history chart works;
- event list works;
- connection state machine tests pass.

### Stage D — Android background and alerts

Done only when:

- status and alert channels work;
- alert-engine test matrix passes;
- independent short-window/weekly thresholds work;
- critical repeat works;
- reset/rearm behavior works;
- all three run modes work;
- live foreground service and periodic worker are mutually exclusive;
- notification permission/channel state is correctly represented.

### Stage E — Real-device usability

Done only when the actual Win11 → Find X8 → HUAWEI Health → WATCH FIT 2 chain passes the real-device matrix and the quota text is readable on the watch.

## 32. Implementation anti-patterns explicitly prohibited

Do not accept any implementation that uses:

- trust-all TLS;
- disabled hostname/identity validation;
- plaintext long-lived tokens;
- hard-coded LAN IP;
- hard-coded `primary == shortWindow` assumptions;
- `Thread.sleep` as connection management;
- `while(true)` quota polling;
- Compose/ViewModel directly owning sockets;
- ignored TLS errors with “continue anyway”;
- history DB failure bringing down current quota service;
- empty catch-all exception handlers;
- duplicate simultaneous foreground-service and periodic-worker synchronization;
- fabricated `0%` values for missing protocol fields.

## 33. Final V1 acceptance scenario

The complete system should pass this scripted acceptance flow:

1. Start Bridge manually on Windows 11.
2. Complete ChatGPT login.
3. Find X8 auto-discovers Bridge.
4. Complete secure pairing.
5. Android shows current short-window and weekly quota.
6. WATCH FIT 2 shows the same core percentages through notification mirroring.
7. Use Codex until a quota update occurs.
8. Android receives and displays the update.
9. WATCH FIT 2 reflects the intended status behavior according to real-device Gate results.
10. Force/test a warning threshold and receive one warning.
11. Force/test a critical threshold and receive one critical alert.
12. Stop Bridge; Android/watch show stale/offline state without inventing fresh values.
13. Restart Bridge; Android reconnects without QR pairing.
14. Change PC LAN IP; Android rediscovers and reconnects without re-pairing.
15. Replace Bridge identity; Android refuses connection and does not send the device credential.
16. Restore the original identity; normal trusted connection resumes.

## 34. Future extension points (not V1 work)

The architecture intentionally leaves room for:

- remote access through a tunnel/remote endpoint provider;
- multiple paired phones/tablets with independent credentials;
- a small-share distribution workflow;
- auto-update/installer improvements;
- longer-term analytics;
- a native wearable app if a future Huawei device/platform makes it worthwhile.

These are not implementation tasks until separately designed and approved.

## 35. Written-spec review checklist

This specification intentionally contains no product-code implementation and no unresolved feature-selection placeholders.

Review should focus on:

1. whether V1 scope is still the desired scope;
2. whether any security/usability requirement is too heavy for a personal tool;
3. whether the no-auto-start decisions remain correct;
4. whether 24-hour history and alert semantics match intended use;
5. whether the real-device WATCH FIT 2 compatibility Gate is acceptable.

Only after this written specification is approved should an implementation plan be produced for WorkBuddy/DeepSeek/Hy4.
