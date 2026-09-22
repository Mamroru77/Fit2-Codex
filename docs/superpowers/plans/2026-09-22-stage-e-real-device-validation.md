# Stage E — Real Device Validation and V1 Release Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Validate the complete V1 on the actual Windows 11 PC, OPPO Find X8, HUAWEI Health, and HUAWEI WATCH FIT 2; fix only evidence-backed compatibility issues; produce a repeatable V1 acceptance record and local install artifacts.

**Architecture:** No new subsystem is planned in this stage. The stage exercises the approved Windows and Android components under network, lifecycle, security, notification, and physical-watch conditions. A WATCH FIT 2 status-refresh compatibility layer may be added only if the explicit ongoing-notification gate fails on the real device.

**Tech Stack:** Existing .NET/Android projects, ADB, Windows PowerShell, physical Wi-Fi/LAN, HUAWEI Health, HUAWEI WATCH FIT 2.

**Spec:** `docs/superpowers/specs/2026-09-22-codex-quota-bridge-design.md`

## Global Constraints

- Do not add a native watch app.
- Do not weaken TLS or pairing to make testing easier.
- Do not introduce remote/cloud connectivity.
- Do not add OPPO-private background hacks.
- Ongoing-notification watch refresh workaround is prohibited unless the physical gate demonstrates stale watch text while Android status text updates correctly.
- Any workaround added after a failed gate must be throttled and quiet; it must not vibrate for ordinary quota changes.

## Review Focus

1. WATCH FIT 2 truncation must still leave short-window and weekly remaining percentages readable.
2. Status refresh must not cause repeated watch vibration during ordinary 1% quota changes.
3. Bridge IP rotation must reconnect without QR re-pair and without accepting a different identity.
4. Phone process/service lifecycle under ColorOS must degrade to explicit offline/resume state, not silently claim real-time sync.
5. Security identity replacement must prove the Android client does not transmit the stored device credential.

---

### Task 1: Produce clean Release builds and a validation checklist record

**Files:**
- Create: `docs/validation/v1-device-validation.md`
- Create: `artifacts/` directory locally (gitignored)
- Modify: `.gitignore`

**Interfaces:**
- Record every test with date, Windows build, Bridge commit, Android APK commit, Find X8 Android/ColorOS version, HUAWEI Health version, WATCH FIT 2 firmware, PASS/FAIL, notes.

- [ ] **Step 1: Write the validation checklist before running tests**

Create rows for every Task 2–6 scenario below and the 16-step final acceptance scenario from the spec. No blank “misc” rows; every row has explicit expected behavior.

- [ ] **Step 2: Run clean automated Release gates**

```powershell
 dotnet test src/windows/CodexQuota.sln -c Release
 cd src/android
 .\gradlew.bat clean testDebugUnitTest lintDebug assembleDebug
 cd ../..
```

Expected: all exit 0.

- [ ] **Step 3: Build local artifacts**

```powershell
 dotnet publish src/windows/CodexQuota.Desktop/CodexQuota.Desktop.csproj -c Release -r win-x64 --self-contained false -o artifacts/windows
 Copy-Item src/android/app/build/outputs/apk/debug/app-debug.apk artifacts/CodexQuota-debug.apk
```

Copy the supported/versioned `codex-app-server.exe` into `artifacts/windows/runtime/` according to Stage A packaging policy.

- [ ] **Step 4: Install Android build and record versions**

```powershell
 adb install -r artifacts/CodexQuota-debug.apk
 adb shell getprop ro.build.version.release
 adb shell getprop ro.build.version.sdk
```

Record outputs plus ColorOS/HUAWEI Health/watch firmware from device UI in the checklist.

- [ ] **Step 5: Commit validation template only**

```powershell
 git add .gitignore docs/validation/v1-device-validation.md
 git commit -m "test: add v1 physical device validation matrix"
```

---

### Task 2: Validate pairing, normal live updates, and cached/offline behavior

**Files:**
- Modify: `docs/validation/v1-device-validation.md` with observed results.

**Interfaces:**
- Physical chain: Win11 Bridge → Find X8 → HUAWEI Health → WATCH FIT 2.

- [ ] **Step 1: Validate automatic discovery and secure initial pairing**

Start Bridge manually, ensure Find X8 is on same Wi-Fi, use auto discovery first. Record whether `_codexquota._tcp` is found, verify displayed Bridge identity/fingerprint confirmation, complete pairing, and confirm Android shows real quota. Then reset pairing and repeat once using QR fallback.

- [ ] **Step 2: Validate normal quota update propagation**

Use Codex until `account/rateLimits/updated` produces a changed percentage or use the Stage A test-source mode only if the real quota cannot be changed safely. Verify Windows current state changes, Android updates without manual refresh, and ongoing notification text updates.

- [ ] **Step 3: Validate Bridge stop/offline behavior**

Exit Bridge from tray. Verify Android enters `OfflineCached`, retains last values/history, shows last-update time, and sends no new threshold alerts from stale data. Verify status notification includes stale/offline wording.

- [ ] **Step 4: Validate Bridge restart**

Restart Bridge. Verify Android reconnects without QR, live state returns, and no previously-fired warning/critical is duplicated simply because the connection resumed.

- [ ] **Step 5: Record PASS/FAIL evidence**

Add timestamps, screenshots/notes paths (local artifacts may remain uncommitted), and exact failures to the validation doc. Do not fix yet unless a failure blocks all remaining tests.

---

### Task 3: Validate LAN/IP/network transitions and security identity behavior

**Files:**
- Modify: `docs/validation/v1-device-validation.md`

**Interfaces:**
- Stable trust anchor is Bridge identity; IP is location only.

- [ ] **Step 1: Force a PC LAN IP change**

Renew DHCP, switch Ethernet/Wi-Fi, or temporarily change router lease so the Bridge endpoint IP changes. Verify certificate leaf/SAN refresh, mDNS advertises new endpoint, Android rediscovers and reconnects without re-pairing.

- [ ] **Step 2: Validate Wi-Fi → cellular → Wi-Fi**

Disable Wi-Fi on Find X8. Verify LAN-only app moves to `OfflineCached` and does not repeatedly dial the private IP. Re-enable Wi-Fi and verify network-change signal triggers immediate rediscovery rather than waiting the maximum backoff.

- [ ] **Step 3: Validate mDNS-unavailable fallback**

Use a guest/AP-isolated network or temporarily block multicast discovery while direct LAN routing remains available where possible. Verify QR/last-known endpoint behavior is understandable and no secret is exposed in discovery TXT data.

- [ ] **Step 4: Validate identity replacement rejection**

Back up the current Bridge identity, intentionally run a test instance with a different identity at the same host/port, and capture client debug instrumentation that records “Authorization header attached = false”. Verify Android enters `SecurityError`. Restore the original identity and verify normal connection resumes without re-pairing.

- [ ] **Step 5: Record results before code changes**

Document each outcome and classify failures as product bug, environment limitation, or expected OS/network behavior.

---

### Task 4: Validate Android run modes, ColorOS lifecycle, permissions, and alert semantics

**Files:**
- Modify: `docs/validation/v1-device-validation.md`

**Interfaces:**
- OFF/OFF, BACKGROUND, LIVE modes from Stage D.

- [ ] **Step 1: Validate the four switch combinations**

Use Android Studio/ADB process/service inspection to confirm OFF/OFF has no FGS/periodic sync, OFF/ON has periodic work only, ON/OFF has connectedDevice FGS only, ON/ON has connectedDevice FGS only.

Useful commands:

```powershell
 adb shell dumpsys activity services com.codexquota.app
 adb shell dumpsys jobscheduler | Select-String codexquota
```

- [ ] **Step 2: Validate notification permission/channel disablement**

Disable all notifications, then only status channel, then only alerts channel in Android system settings. Return to app each time and verify Settings reports the real OS state. Restore permissions afterward.

- [ ] **Step 3: Validate warning/critical/repeat behavior on real phone**

Temporarily configure thresholds around the current trusted quota so a controlled fresh snapshot crosses warning, then critical. Verify one warning, one critical, 25→8 equivalent jump produces only critical, and repeat occurs only after configured interval while fresh. Restore preferred thresholds afterward.

- [ ] **Step 4: Validate process/service stop and reboot behavior**

Force-stop/process-kill where appropriate, reopen app, verify persisted alert state prevents duplicate critical. Reboot phone with live preference enabled: verify live service does not auto-start and UI offers resume; resume manually and verify connection returns.

- [ ] **Step 5: Record ColorOS-specific observations without adding vendor hacks**

If ColorOS limits background work, record exact system behavior and public setting needed. Do not add private intents/components.

- [ ] **Step 6: Run Windows/Android performance and power sanity checks**

With Bridge connected but idle for at least 5 minutes, sample the Windows process and record working set plus CPU delta; the design target is near-zero idle CPU and roughly `<150 MB` typical working set, treated as an investigation threshold rather than a release crash line. Confirm `/api/v1/quota` can be refreshed repeatedly without spawning Codex RPC activity or SQLite reads in structured logs. On Find X8 verify `MODE_OFF` has no recurring network/jobs, `MODE_BACKGROUND` has network activity only when the Worker runs, and `MODE_LIVE` maintains one WebSocket with heartbeat in the 30–60 second range and no 1-second polling. Use:

```powershell
 Get-Process CodexQuota* | Select-Object ProcessName,Id,CPU,WorkingSet64
 adb shell dumpsys jobscheduler | Select-String codexquota
 adb shell dumpsys activity services com.codexquota.app
```

If Android Studio Energy Profiler or `adb shell dumpsys batterystats` shows repeated wakeups/network loops while otherwise idle, record FAIL and fix before Stage E closes. Do not invent a fixed battery-percentage requirement.

---

### Task 5: Execute WATCH FIT 2 notification gate

**Files:**
- Modify: `docs/validation/v1-device-validation.md`
- Conditionally create only if gate fails: `src/android/app/src/main/java/com/codexquota/app/notifications/WatchStatusRefreshCompat.kt`
- Conditionally create only if gate fails: `src/android/app/src/test/java/com/codexquota/app/notifications/WatchStatusRefreshCompatTest.kt`

**Interfaces:**
- Priority on watch: short remaining, weekly remaining, short reset, weekly reset.

- [ ] **Step 1: Validate test notification path**

Use Settings “Send test notification”. Confirm it appears on WATCH FIT 2 through HUAWEI Health. Record title/body line count, truncation, and whether Chinese/compact formats are readable.

- [ ] **Step 2: Validate ongoing status-update propagation without workaround**

With live status ON, note current watch text, then cause Android to update the same status notification ID with changed quota text. Observe whether WATCH FIT 2 content updates and whether it vibrates/re-alerts.

- [ ] **Step 3: Apply the gate decision**

If watch text updates correctly: record PASS and create no compatibility code.

If Android notification updates but watch remains stale: write a failing `WatchStatusRefreshCompatTest` that requires a quiet refresh event no more often than a chosen throttle (start with 5 minutes) and never on every 1% update; then implement a compatibility class that posts a separate silent refresh event only when rendered status text materially changed and throttle elapsed.

- [ ] **Step 4: Re-run watch behavior after any evidence-backed workaround**

Verify fresh quota reaches watch, ordinary changes do not vibrate repeatedly, warning/critical alerts still use the alert channel, and compact text keeps both percentages visible.

- [ ] **Step 5: Commit only evidence-backed compatibility changes/results**

```powershell
 git add docs/validation src/android
 git commit -m "test: validate watch fit 2 notification behavior"
```

If no code changed, the commit may contain validation documentation only.

---

### Task 6: Run the 16-step final acceptance scenario and close V1

**Files:**
- Modify: `docs/validation/v1-device-validation.md`
- Create: `docs/validation/v1-release-summary.md`

**Interfaces:**
- Final decision is PASS only when every required acceptance item passes or a documented environment limitation is explicitly accepted by the user without weakening security/correctness.

- [ ] **Step 1: Run automated suites one final time from clean tree**

```powershell
 dotnet test src/windows/CodexQuota.sln -c Release
 cd src/android
 .\gradlew.bat clean testDebugUnitTest lintDebug connectedDebugAndroidTest assembleDebug
 cd ../..
```

Expected: PASS.

- [ ] **Step 2: Execute spec section 33 steps 1–8 exactly**

Start Bridge, login, discover, securely pair, show both quotas, show watch core percentages, use Codex until update, confirm Android receives it. Mark each row PASS/FAIL.

- [ ] **Step 3: Execute spec section 33 steps 9–16 exactly**

Validate watch status behavior, warning, critical, stale/offline, restart no re-pair, IP change no re-pair, replaced identity refusal/no credential, original identity recovery.

- [ ] **Step 4: Write release summary**

Summarize tested versions, automated test commands/results, all physical matrix outcomes, any accepted limitations, watch gate result, and exact commit/tag considered V1. Do not claim “fully tested” if any physical row remains unexecuted.

- [ ] **Step 5: Commit and tag V1 candidate**

```powershell
 git add docs/validation
 git commit -m "test: complete codex quota bridge v1 acceptance"
 git tag v1.0.0-rc1
```
