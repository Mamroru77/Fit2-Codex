# V1 Device Validation Record

**Status:** `DEVELOPMENT COMPLETE` · `AUTOMATED VERIFICATION COMPLETE` · `PHYSICAL STAGE E VALIDATION PENDING`

Every automated gate the approved plans name is green. The physical acceptance matrix below has not
been run, and no row in it claims a result.

```text
WINDOWS DEVELOPMENT COMPLETE
STAGE B COMPLETE
STAGE C COMPLETE
STAGE D COMPLETE
PHYSICAL STAGE E VALIDATION PENDING
```

## Verified commits and runs

| Item | Value |
|---|---|
| Verified commit (both Android gates) | `d241a551a67742f033a017dbbd59e2d392d139` |
| Tags | `stage-c-android-foundation` and `stage-d-android-alerts`, both on that commit |
| Final CI run | `36039193067` — both jobs `success` |
| Emulator API | **36** (`system-images;android-36;google_apis;x86_64`) |

Both Android tags point at the same commit, because the Stage C and Stage D runtime gates were
implemented together and this is the first commit at which either of them ran green. A tag on an
earlier commit would name a gate that was never green.

## Automated gates

| Gate | Where | Result |
|---|---|---|
| Windows Release suite | `dotnet test src/windows/CodexQuota.sln -c Release` | **266 passed, 0 failed** |
| Real Codex runtime | `dotnet run -c Release --project tests/windows/CodexQuota.RealRuntimeCheck` | PASS (4/4 checks) |
| Android JVM unit tests | `gradlew :app:testDebugUnitTest` | **201 passed, 0 failed** |
| Android instrumentation | `gradlew :app:connectedDebugAndroidTest` on API 36 | **37 passed, 0 failed** |
| Android delivery-disabled gate | `adb shell am instrument` with `POST_NOTIFICATIONS` revoked | **3 passed, 0 failed** |
| Android lint | `gradlew :app:lintDebug` | **PASS** — no errors |
| Android assemble | `gradlew :app:assembleDebug` | **PASS** |
| APK | `artifacts/android/app-debug.apk` | 37,001,119 bytes, SHA-256 `28046f7f7106e245f73a3d69644e3fedc07ac0e1ea0341c889684f12f70be197` |

Instrumentation is 40 tests across two invocations. They are separate because the denied-delivery
tests need `POST_NOTIFICATIONS` revoked, and revoking a runtime permission kills the process that
holds it — so that state is set from `adb` between instrumentation processes, and
`connectedDebugAndroidTest` excludes the class by name.

## Devices and versions

| Item | Value |
|---|---|
| Windows build | _to be recorded on the day_ |
| Verified commit | `d241a55` |
| Android APK commit | `d241a55` |
| OPPO Find X8 Android / ColorOS | _to be recorded on the day_ |
| HUAWEI Health version | _to be recorded on the day_ |
| HUAWEI WATCH FIT 2 firmware | _to be recorded on the day_ |

## Physical acceptance matrix

Legend: **PENDING_MANUAL_DEVICE_VALIDATION** = requires the real chain below.

```text
Windows 11  ->  OPPO Find X8  ->  HUAWEI Health  ->  HUAWEI WATCH FIT 2
```

| # | Scenario | Expected behaviour | Result |
|---|---|---|---|
| 1 | Start Bridge manually on Windows 11 | Tray appears; no auto-start is registered | PENDING_MANUAL_DEVICE_VALIDATION |
| 2 | Complete ChatGPT login | Browser flow; Bridge never sees the password | PENDING_MANUAL_DEVICE_VALIDATION |
| 3 | Choose the LAN interface once in Settings | Bridge listens on that address only | PENDING_MANUAL_DEVICE_VALIDATION |
| 4 | Find X8 auto-discovers the Bridge | One `_codexquota._tcp` record; TXT has only bridgeId/apiVersion/tls | PENDING_MANUAL_DEVICE_VALIDATION |
| 5 | Pair by QR fallback | QR carries no credential; phone shows the same identity code | PENDING_MANUAL_DEVICE_VALIDATION |
| 6 | Approve pairing on Windows | Only the local Allow click issues a credential | PENDING_MANUAL_DEVICE_VALIDATION |
| 7 | Dashboard shows short-window and weekly quota | Both percentages and reset times match Windows | PENDING_MANUAL_DEVICE_VALIDATION |
| 8 | WATCH FIT 2 shows the core percentages | Readable through HUAWEI Health notification mirroring | PENDING_MANUAL_DEVICE_VALIDATION |
| 9 | Use Codex until quota moves | Android receives and displays the update | PENDING_MANUAL_DEVICE_VALIDATION |
| 10 | Force a warning threshold | Exactly one warning notification | PENDING_MANUAL_DEVICE_VALIDATION |
| 11 | Force a critical threshold | Exactly one critical alert | PENDING_MANUAL_DEVICE_VALIDATION |
| 12 | Stop the Bridge | Android and watch show stale/offline; no invented fresh values | PENDING_MANUAL_DEVICE_VALIDATION |
| 13 | Restart the Bridge | Android reconnects without QR pairing | PENDING_MANUAL_DEVICE_VALIDATION |
| 14 | Change the PC LAN IP | Android rediscovers and reconnects without re-pairing | PENDING_MANUAL_DEVICE_VALIDATION |
| 15 | Replace the Bridge identity | Android refuses and does **not** send the device credential | PENDING_MANUAL_DEVICE_VALIDATION |
| 16 | Restore the original identity | Normal trusted connection resumes | PENDING_MANUAL_DEVICE_VALIDATION |
| 17 | Wi-Fi to cellular and back | `OFFLINE_CACHED`, then immediate rediscovery on return | PENDING_MANUAL_DEVICE_VALIDATION |
| 18 | Compact and Chinese watch text | Percentages readable; truncation still leaves both values visible | PENDING_MANUAL_DEVICE_VALIDATION |
| 19 | Status updates cause no unwanted vibration | Ordinary quota changes are silent | PENDING_MANUAL_DEVICE_VALIDATION |

## WATCH FIT 2 ongoing-notification gate

The plan prohibits building a compatibility refresh layer before the physical evidence exists.

| Question | Result |
|---|---|
| Does updating an existing ongoing Android notification refresh the WATCH FIT 2 text? | PENDING_MANUAL_DEVICE_VALIDATION |

**Decision rule:** if the watch retains stale text while the Android status text updates correctly,
then and only then implement the throttled, quiet refresh-event layer. Until that is observed, no
workaround is implemented — and none has been.

### Exact reproduction steps for this gate

1. Pair the Find X8 and enable the status notification (`MODE_LIVE`).
2. Confirm the WATCH FIT 2 shows `5h <n>% | W <n>%`.
3. On Windows, wait for a real quota change (or use Codex until the percentage moves).
4. Confirm the Android ongoing notification text changed in place (same notification id).
5. Look at the WATCH FIT 2 **without** unlocking or interacting with the phone.
6. Record: does the watch text match the new Android text, or is it still the previous value?
7. Repeat with a change large enough to be unmistakable (e.g. 30% or more).
8. Record whether any of the updates caused the watch to vibrate.

## Android status

Stage C and Stage D are implemented, and both runtime gates are green on a GitHub-hosted runner.
That runner is the authoritative environment because this development machine cannot reach
`dl.google.com` or `maven.google.com`:

| Host | From this machine | From a GitHub runner |
|---|---|---|
| `dl.google.com/android/repository` | `curl: (7) CONNECT tunnel failed, response 502` | HTTP 200 |
| `dl.google.com/dl/android/maven2` (AGP) | `curl: (7) CONNECT tunnel failed, response 502` | HTTP 200 |
| `maven.google.com` | `curl: (35) schannel: handshake failed` | HTTP 200 |

The application also builds, lints and passes its unit tests **on this machine**, by supplying the
toolchain from reachable mirrors. That is how the runtime gate's failures were diagnosed in minutes
rather than in CI round trips, and it is why the gate found six product defects rather than one.

### What the runtime gate found

It was not a formality. Running the instrumentation suite on a real Android runtime exposed six
defects that no JVM test could have:

1. `PairedBridgeTrustManagerFactory` built its trust manager from `CertPathTrustManagerParameters`,
   which Android rejects. **Every authenticated REST and WebSocket request would have failed on a
   real device**, after pairing had appeared to succeed.
2. `MainActivity` built its own `AppContainer`, giving the process two pairing stores, two Room
   databases, two connection managers and two DataStores for one file.
3. `LiveBridgeService` posted its foreground notification without creating the channel it names.
4. The `connectedDevice` foreground service declared none of Android 14's prerequisites, so
   `startForeground` threw and live mode could not start at all.
5. `CodexQuotaWorkerFactory` was never installed, so background mode would have enqueued a worker
   that always failed.
6. Both notifications used framework drawables as their small icon, which `startForeground` rejects.

### What is not verified, and why

- **The physical chain.** Rows 1–19 below need the real devices.
- **`NsdBridgeDiscovery`.** Compiled and linted, but not unit-tested: the real `NsdManager` needs a
  device, and a fake would only test the fake. mDNS discovery remains part of the physical chain.
- **`KeystoreSecretBox`** is now covered on a device by `KeystoreSecretBoxTest` — round trip, no
  plaintext in the sealed bytes, and a tampered ciphertext or IV failing the GCM tag.

## Install commands

Windows (once the package is published to `artifacts/windows`):

```powershell
# Place the supported Codex runtime beside the executable:
#   artifacts\windows\runtime\codex.exe   (the Codex CLI; app-server is its subcommand)
artifacts\windows\CodexQuota.Desktop.exe
```

Android — the verified build is `artifacts/android/app-debug.apk` (SHA-256
`28046f7f7106e245f73a3d69644e3fedc07ac0e1ea0341c889684f12f70be197`), and the same APK is inside the
`android-reports` artifact of CI run `36039193067`:

```powershell
adb install -r artifacts\android\app-debug.apk
```

Or build it locally:

```powershell
cd src\android
.\gradlew.bat assembleDebug
adb install -r app\build\outputs\apk\debug\app-debug.apk
```

## Diagnostics

| What | Where |
|---|---|
| Redacted Bridge log | `%LOCALAPPDATA%\CodexQuotaBridge\logs\bridge.log` (5 MB, 5 rotated files) |
| Bridge database | `%LOCALAPPDATA%\CodexQuotaBridge\bridge.db` |
| Bridge identity metadata (non-secret) | `%LOCALAPPDATA%\CodexQuotaBridge\identity\bridge-identity.json` |
| Real-runtime check | `dotnet run -c Release --project tests/windows/CodexQuota.RealRuntimeCheck` |
| Bridge health | `https://<bridge-ip>:47821/api/v1/health` (anonymous, discloses nothing) |

Never paste a device credential, an `Authorization` header or a private key into a report. The log
redactor removes them before they reach disk, but a manual copy would not.
