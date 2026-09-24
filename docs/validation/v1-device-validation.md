# V1 Device Validation Record

**Status:** `ANDROID DEVELOPMENT IN PROGRESS` · `PHYSICAL STAGE E VALIDATION PENDING`

Earlier revisions of this file said `DEVELOPMENT COMPLETE` while also saying that Stage C and Stage D
were not implemented. That was premature and has been corrected. The truthful status is:

```text
WINDOWS DEVELOPMENT COMPLETE
STAGE B COMPLETE
ANDROID DEVELOPMENT IN PROGRESS
PHYSICAL STAGE E VALIDATION PENDING
```

Android development is "in progress" rather than complete because the approved plans' instrumented
tests (Stage C Task 8 and Stage D Task 8) have not been written or run. Everything that can be
verified without a device is verified below.

Every row below is an acceptance criterion from the approved spec (§33) and the Stage E plan. Rows
that require the physical chain are marked **PENDING_MANUAL_DEVICE_VALIDATION** and were **not**
run. Nothing in this file claims a physical result that was not observed.

## Devices and versions

| Item | Value |
|---|---|
| Windows build | _to be recorded on the day_ |
| Bridge commit (final) | `d28514c` |
| Android APK commit | `d28514c` — the first commit at which the Android application builds |
| OPPO Find X8 Android / ColorOS | _to be recorded on the day_ |
| HUAWEI Health version | _to be recorded on the day_ |
| HUAWEI WATCH FIT 2 firmware | _to be recorded on the day_ |

## Automated gates already green

| Gate | Command | Result |
|---|---|---|
| Windows Release suite | `dotnet test src/windows/CodexQuota.sln -c Release` | 266 passed, 0 failed |
| Real Codex runtime | `dotnet run -c Release --project tests/windows/CodexQuota.RealRuntimeCheck` | PASS (4/4 checks) |
| Android unit tests | `gradlew :app:testDebugUnitTest` | 168 passed, 0 failed |
| Android build + lint (CI) | GitHub Actions `Android`, run `35997777459` | see the Android status section |
| Android toolchain (CI) | GitHub Actions `Android`, run `35978620889` | success |

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

Stage C and Stage D are implemented. The application builds, lints and passes its unit tests on a
GitHub-hosted runner, which is the authoritative gate because this development machine cannot reach
`dl.google.com` or `maven.google.com`:

| Host | From this machine | From a GitHub runner |
|---|---|---|
| `dl.google.com/android/repository` | `curl: (7) CONNECT tunnel failed, response 502` | HTTP 200 |
| `dl.google.com/dl/android/maven2` (AGP) | `curl: (7) CONNECT tunnel failed, response 502` | HTTP 200 |
| `maven.google.com` | `curl: (35) schannel: handshake failed` | HTTP 200 |

| Gate | Where | Result |
|---|---|---|
| `testDebugUnitTest` | CI run `35997777459` | PASS — the task ran and the build succeeded; the same 168 tests pass on this machine |
| `lintDebug` | CI run `35997777459` | PASS — no errors (lint aborts the build on any) |
| `assembleDebug` | CI run `35997777459` | PASS |
| Build | CI run `35997777459` | `BUILD SUCCESSFUL in 3m 24s`, 53 tasks executed |
| APK artifact | CI run `35997777459` | `android-reports.zip`, 12,979,799 bytes, artifact id `10806923407` |
| APK path inside the artifact | — | `outputs/apk/debug/app-debug.apk` (the artifact root is `app/build`) |
| Local APK | this machine | `artifacts/android/app-debug.apk`, 36,497,614 bytes, SHA-256 `8e593df8ebf95d3394c8271a9119966bf099a1d29a81a68e5d0882615a61b8f0` |

The CI artifact of run `35997777459` predates a workflow fix that adds `app/build/test-results/**`
to the uploaded paths. That run's JUnit XML was therefore not preserved, so its unit-test result rests
on the task having run and the build having succeeded, plus the identical suite passing locally. Later
runs carry the XML.

The same 168 tests also pass on this machine, which is how the five defects listed in the execution
ledger were found.

### What is not verified, and why

- **Instrumented tests.** The approved plans' Stage C Task 8 and Stage D Task 8 call for
  `connectedDebugAndroidTest`. No instrumentation source has been written, because there is no
  emulator on this machine and the CI job does not start one. Rows 4–19 below therefore cannot be
  attempted from either environment yet.
- **The `_codexquota._tcp` discovery adapter** is compiled and linted but has no automated test: the
  real `NsdManager` needs a device, and a fake would only test the fake.
- **`KeystoreSecretBox`** is compiled and linted but not unit-tested: the Android Keystore is not
  available on the JVM. Its contract is narrow (seal/open with AES/GCM under a non-exportable key) and
  everything above it depends on the `SecretBox` interface, which is tested with an authenticated
  fake.

## Install commands

Windows (once the package is published to `artifacts/windows`):

```powershell
# Place the supported Codex runtime beside the executable:
#   artifacts\windows\runtime\codex.exe   (the Codex CLI; app-server is its subcommand)
artifacts\windows\CodexQuota.Desktop.exe
```

Android (download `android-reports.zip` from CI run `35997777459`, then):

```powershell
adb install -r app-debug.apk
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
