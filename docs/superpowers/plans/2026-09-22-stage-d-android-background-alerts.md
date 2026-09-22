# Stage D — Android Background Sync, Alerts and Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add deterministic per-phone alert rules, persistent notification/status behavior, and the three approved Android run modes: OFF, low-power WorkManager background sync, and live `connectedDevice` foreground-service sync.

**Architecture:** A pure `ThresholdEvaluator` produces alert actions from trusted snapshots/rules/state. `SyncModeController` is the only owner of mode transitions and ensures `LiveBridgeService` and unique periodic WorkManager are mutually exclusive. Notification rendering/delivery is separate from alert evaluation, and alert state is persisted so process recreation does not duplicate notifications.

**Tech Stack:** Kotlin/coroutines, Room/DataStore, WorkManager, Android notification channels, `ServiceCompat.startForeground`, JUnit/Robolectric, existing OkHttp/repositories from Stage C.

**Spec:** `docs/superpowers/specs/2026-09-22-codex-quota-bridge-design.md`

## Global Constraints

- `MODE_OFF`: no foreground service and no periodic worker.
- `MODE_BACKGROUND`: no long-lived WebSocket; exactly one unique 15-minute periodic worker.
- `MODE_LIVE`: `LiveBridgeService` with `foregroundServiceType="connectedDevice"`; periodic worker cancelled.
- Never use `dataSync` as the all-day live foreground-service type.
- Do not start live sync from `BOOT_COMPLETED`; after reboot, settings remain but user must resume live sync from the app.
- Status notification and low-quota alerts are independent switches.
- Short-window and weekly warning/critical thresholds are independent; defaults 20/10, valid 1–99, critical < warning.
- Repeated reminders apply only to critical, default OFF, and only while data is fresh/online.
- Stale/offline data never creates a new threshold-crossing alert or repeated critical reminder.
- Ordinary quota changes update the ongoing status notification but never create alert-channel notifications.

## Review Focus

1. One update jumping 25% → 8% must emit one critical alert, not warning then critical.
2. Persisted alert state after process death must suppress duplicates for an already-triggered critical state.
3. Toggling status ON while a periodic worker exists must cancel the worker before live connection starts.
4. Notification permission/channel disabled state must be reflected in UI rather than reporting healthy notification delivery.
5. Explicitly stopped foreground service must not be immediately resurrected by an auto-loop.

---

### Task 1: Implement alert rules, state and pure threshold evaluator

**Files:**
- Create: `src/android/app/src/main/java/com/codexquota/app/domain/alerts/AlertRules.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/domain/alerts/AlertState.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/domain/alerts/AlertAction.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/domain/alerts/ThresholdEvaluator.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/domain/alerts/ThresholdEvaluatorTest.kt`

**Interfaces:**
- `fun evaluate(previous: QuotaSnapshot?, current: QuotaSnapshot, rules: AlertRules, state: AlertState, now: Instant, fresh: Boolean): AlertEvaluation`
- `AlertEvaluation` returns updated state plus zero or more actions.

- [ ] **Step 1: Write the full approved alert matrix as failing unit tests**

Include exactly: 25→19 warning; 19→18 no repeat; 19→9 critical; 25→9 critical only; 9→100 reset/rearm; 9→12 critical rearmed but warning remains triggered; 19→21 warning rearmed; first snapshot 18 warning; first snapshot 8 critical only; stale snapshot no new alert; critical repeat only after interval and only fresh; weekly rules independent from short-window rules.

- [ ] **Step 2: Run alert tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.domain.alerts.ThresholdEvaluatorTest"
```

Expected: FAIL.

- [ ] **Step 3: Implement deterministic evaluator**

Model each quota window state as `Normal`, `WarningTriggered`, or `CriticalTriggered` with persisted last-critical-notification UTC plus in-process elapsed marker supplied by caller. Detect window reset before threshold evaluation by reset timestamp/window identity change and upward jump consistent with reset; clear that window's alert state first.

- [ ] **Step 4: Run alert tests**

```powershell
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.domain.alerts.ThresholdEvaluatorTest"
```

Expected: PASS.

- [ ] **Step 5: Commit alert domain**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): add deterministic quota alert engine"
```

---

### Task 2: Persist alert/settings state and validate threshold configuration

**Files:**
- Create: `src/android/app/src/main/java/com/codexquota/app/data/settings/AlertSettingsStore.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/settings/NotificationPreferences.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/db/AlertStateEntity.kt`
- Modify: `src/android/app/src/main/java/com/codexquota/app/data/db/AppDatabase.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/data/settings/AlertSettingsStoreTest.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/data/AlertStateRestorationTest.kt`

**Interfaces:**
- Settings store exposes `Flow<NotificationPreferences>`.
- Repository persists per-window `AlertState` atomically after every evaluation.

- [ ] **Step 1: Write validation/restoration tests**

Assert `warning=20 critical=10` saves; `critical >= warning`, `0`, and `100` are rejected. Persist a critical-triggered state, recreate repository/evaluator, feed unchanged 8% snapshot, and assert no second initial critical action is emitted.

- [ ] **Step 2: Run tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.data.settings.*" --tests "com.codexquota.app.data.AlertStateRestorationTest"
```

Expected: FAIL.

- [ ] **Step 3: Implement DataStore settings and Room alert state**

Defaults: both quota windows warning 20, critical 10; status notification ON/OFF default can remain OFF until user chooses; low-quota alerts default OFF; repeat OFF, interval 60 minutes; watch format default compact. Persist alert state in Room, not DataStore, because it changes with snapshots.

- [ ] **Step 4: Run persistence tests**

```powershell
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.data.settings.*" --tests "com.codexquota.app.data.AlertStateRestorationTest"
```

Expected: PASS.

- [ ] **Step 5: Commit settings/state**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): persist alert rules and trigger state"
```

---

### Task 3: Create notification channels and pure notification renderers

**Files:**
- Create: `src/android/app/src/main/java/com/codexquota/app/notifications/NotificationChannels.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/notifications/StatusNotificationRenderer.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/notifications/AlertNotificationRenderer.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/notifications/QuotaNotificationManager.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/notifications/NotificationRendererTest.kt`

**Interfaces:**
- Channel IDs: `codex_status`, `codex_alerts`.
- One fixed status notification ID; alerts use unique event IDs.
- Renderers consume domain state and return a plain render model before Android `Notification` creation.

- [ ] **Step 1: Write compact/Chinese/offline renderer tests**

Assert compact fresh model contains `5h 72% | W 54%`; Chinese contains `5小时 72% | 周 54%`; stale model necessarily contains `离线`/localized stale marker plus last-update time; warning/critical model identifies correct window and percentage. Ordinary status render must set alert/vibration intent false.

- [ ] **Step 2: Run notification tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.notifications.NotificationRendererTest"
```

Expected: FAIL.

- [ ] **Step 3: Implement channels and renderer/delivery split**

Create `codex_status` at LOW/default silent and `codex_alerts` at DEFAULT/HIGH user-visible importance on first app initialization. Delivery manager updates status by the same ID and posts warning/critical only from evaluator actions.

- [ ] **Step 4: Run notification tests**

```powershell
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.notifications.*"
```

Expected: PASS.

- [ ] **Step 5: Commit notifications**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): add status and quota alert notifications"
```

---

### Task 4: Implement SyncModeController with strict FGS/WorkManager mutual exclusion

**Files:**
- Create: `src/android/app/src/main/java/com/codexquota/app/sync/SyncMode.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/sync/SyncModeController.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/sync/SyncScheduler.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/sync/SyncModeControllerTest.kt`

**Interfaces:**
- Mode decision: `(status=false, alerts=false) -> Off`; `(false,true) -> Background`; `(true,*) -> Live`.
- `SyncScheduler` methods: `startLive()`, `stopLive()`, `enqueuePeriodic()`, `cancelPeriodic()`.
- Unique work name: `codex_quota_periodic_sync`.

- [ ] **Step 1: Write all four switch-combination tests plus transition ordering**

Assert OFF/OFF stops live and cancels periodic; OFF/ON stops live then enqueues periodic; ON/OFF and ON/ON cancel periodic before starting live. Test repeated application of the same mode is idempotent and does not enqueue duplicate work.

- [ ] **Step 2: Run mode tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.sync.SyncModeControllerTest"
```

Expected: FAIL.

- [ ] **Step 3: Implement mode controller and scheduler abstraction**

`SyncModeController` collects preferences in application scope and serializes transitions with a `Mutex`. `SyncScheduler` calls `workManager.enqueueUniquePeriodicWork(WORK_NAME, ExistingPeriodicWorkPolicy.UPDATE, PeriodicWorkRequestBuilder<BackgroundSyncWorker>(15, TimeUnit.MINUTES).build())` and uses explicit foreground-service start/stop intents.

- [ ] **Step 4: Run mode tests**

```powershell
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.sync.SyncModeControllerTest"
```

Expected: PASS.

- [ ] **Step 5: Commit mode controller**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): coordinate off background and live sync modes"
```

---

### Task 5: Implement connectedDevice LiveBridgeService

**Files:**
- Modify: `src/android/app/src/main/AndroidManifest.xml`
- Create: `src/android/app/src/main/java/com/codexquota/app/sync/LiveBridgeService.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/sync/LiveSyncCoordinator.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/sync/LiveSyncCoordinatorTest.kt`

**Interfaces:**
- Manifest permissions: `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_CONNECTED_DEVICE`, `CHANGE_WIFI_MULTICAST_STATE`, `POST_NOTIFICATIONS` where applicable.
- Service declares `android:foregroundServiceType="connectedDevice"`.
- Coordinator consumes new trusted snapshots, persists/evaluates alert state, updates status, emits alert notifications.

- [ ] **Step 1: Write live-coordinator tests**

Assert connected fresh update writes cache → evaluates alert → updates status in that order; disconnect switches status to stale/offline; a stale callback never triggers a new threshold alert; repeated same snapshot/sequence does not evaluate twice.

- [ ] **Step 2: Run live tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.sync.LiveSyncCoordinatorTest"
```

Expected: FAIL.

- [ ] **Step 3: Implement service and coordinator**

Call `startForeground` immediately with current/cached status notification before long-running connection work. `LiveBridgeService` starts/owns `ConnectionManager` in service scope and stops gracefully on explicit stop action. Do not register `BOOT_COMPLETED` receiver.

- [ ] **Step 4: Run live tests and inspect manifest**

```powershell
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.sync.LiveSyncCoordinatorTest"
 .\gradlew.bat processDebugMainManifest
```

Expected: PASS; merged manifest shows `connectedDevice`, not `dataSync`.

- [ ] **Step 5: Commit live FGS**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): add connected-device live bridge service"
```

---

### Task 6: Implement BackgroundSyncWorker and repeat-critical scheduling

**Files:**
- Create: `src/android/app/src/main/java/com/codexquota/app/sync/BackgroundSyncWorker.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/alerts/AlertProcessingRepository.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/sync/BackgroundSyncWorkerTest.kt`

**Interfaces:**
- Worker performs one short REST refresh; no WebSocket.
- On success: trusted cache → evaluate alerts → persist state → deliver required alert; then returns success.
- On local network unavailable: no alert, return retry/success according to WorkManager semantics without busy loop.

- [ ] **Step 1: Write worker tests**

Test fresh critical triggers once; stale/network failure sends none; Bridge auth required sends none but updates source status; second run before repeat interval sends none; run after repeat interval with fresh <=critical sends repeat; status notification OFF means worker does not create ongoing status notification.

- [ ] **Step 2: Run worker tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.sync.BackgroundSyncWorkerTest"
```

Expected: FAIL.

- [ ] **Step 3: Implement one-shot worker**

Use injected repository via Worker factory. Do not call `setForeground()` for ordinary periodic work. `AlertProcessingRepository` centralizes evaluate/persist/deliver logic so live service and worker share semantics.

- [ ] **Step 4: Run worker/alert tests**

```powershell
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.sync.BackgroundSyncWorkerTest" --tests "com.codexquota.app.domain.alerts.*"
```

Expected: PASS.

- [ ] **Step 5: Commit background alerts**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): add low-power quota alert worker"
```

---

### Task 7: Add notification permission/channel health UI and reboot-resume behavior

**Files:**
- Create: `src/android/app/src/main/java/com/codexquota/app/notifications/NotificationHealthChecker.kt`
- Modify: `src/android/app/src/main/java/com/codexquota/app/ui/settings/SettingsScreen.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/ui/settings/SettingsViewModel.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/ui/settings/SettingsViewModelTest.kt`
- Create: `src/android/app/src/androidTest/java/com/codexquota/app/NotificationPermissionTest.kt`

**Interfaces:**
- UI exposes OS permission, status-channel enabled, alerts-channel enabled, live-service running, LAN permission/network availability, and Bridge reachability.
- After reboot/app process cold start with live setting ON but service not running, UI exposes `liveResumeRequired=true` and a user action starts live service.

- [ ] **Step 1: Write health/resume ViewModel tests**

Assert app-level status switch ON + OS notification permission denied yields “system disabled” state and does not claim healthy delivery. Assert persisted live preference plus `serviceRunning=false` yields resume-required; invoking resume requests permission if needed and then scheduler starts live.

- [ ] **Step 2: Run settings tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.ui.settings.SettingsViewModelTest"
```

Expected: FAIL.

- [ ] **Step 3: Implement public-API-only health checks**

Use `NotificationManagerCompat.areNotificationsEnabled()` and channel importance checks; use standard Android Settings intents only. Do not reference OPPO package/component names. Add “Send test notification” action through `QuotaNotificationManager`.

- [ ] **Step 4: Run unit/instrumentation checks**

```powershell
 .\gradlew.bat testDebugUnitTest connectedDebugAndroidTest
```

Expected: PASS on attached device/emulator.

- [ ] **Step 5: Commit settings/permission health**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): expose notification and live-sync health"
```

---

### Task 8: Stage D full runtime gate

**Files:**
- Create: `src/android/app/src/androidTest/java/com/codexquota/app/StageDRuntimeIntegrationTest.kt`
- Modify only as required by failing integration tests in Stage D-owned files.

**Interfaces:**
- End-to-end Android runtime against a controlled fake Bridge.

- [ ] **Step 1: Write runtime integration scenarios**

Automate: OFF/OFF has no FGS and no unique worker; OFF/ON has worker only; ON/OFF has FGS only; ON/ON has FGS only; 25→9 produces one critical alert; process recreation with persisted critical state produces none; forcing stale/offline pauses repeat; restoring fresh after interval permits repeat; explicit stop action leaves service stopped until user resumes.

- [ ] **Step 2: Run Stage D integration and verify any remaining failures**

```powershell
 cd src/android
 .\gradlew.bat connectedDebugAndroidTest
```

Expected: initial failures guide final wiring only; do not weaken assertions.

- [ ] **Step 3: Fix only integration gaps within the approved architecture**

Do not add boot auto-start, OPPO-private intents, `dataSync`, duplicate periodic work, or notification bypasses. Keep evaluator pure and keep UI out of service/network ownership.

- [ ] **Step 4: Run complete Android verification**

```powershell
 .\gradlew.bat testDebugUnitTest lintDebug connectedDebugAndroidTest assembleDebug
```

Expected: PASS.

- [ ] **Step 5: Commit and tag Stage D**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): complete quota background modes and alerts"
 git tag stage-d-android-alerts
```
