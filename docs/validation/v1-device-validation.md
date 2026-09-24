# V1 Device Validation Record

**Status:** `DEVELOPMENT COMPLETE` · `AUTOMATED VERIFICATION COMPLETE` · `PHYSICAL STAGE E VALIDATION PENDING`

Every row below is an acceptance criterion from the approved spec (§33) and the Stage E plan. Rows
that require the physical chain are marked **PENDING_MANUAL_DEVICE_VALIDATION** and were **not**
run. Nothing in this file claims a physical result that was not observed.

## Devices and versions

| Item | Value |
|---|---|
| Windows build | _to be recorded on the day_ |
| Bridge commit | `10dcef0` (fill in the exact commit used for the run) |
| Android APK commit | _not produced — see the Android status below_ |
| OPPO Find X8 Android / ColorOS | _to be recorded on the day_ |
| HUAWEI Health version | _to be recorded on the day_ |
| HUAWEI WATCH FIT 2 firmware | _to be recorded on the day_ |

## Automated gates already green

| Gate | Command | Result |
|---|---|---|
| Windows Release suite | `dotnet test src/windows/CodexQuota.sln -c Release` | 266 passed, 0 failed |
| Real Codex runtime | `dotnet run -c Release --project tests/windows/CodexQuota.RealRuntimeCheck` | PASS (4/4 checks) |
| Android toolchain (CI) | GitHub Actions `Android`, run 35978620889 | success |

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

Stage C and Stage D have **not** been implemented, so there is no APK and rows 4–19 cannot be
attempted yet. The blocker was the development machine's network, not the plan:

| Host | From this machine |
|---|---|
| `dl.google.com/android/repository` | `curl: (7) CONNECT tunnel failed, response 502` |
| `dl.google.com/dl/android/maven2` (AGP) | `curl: (7) CONNECT tunnel failed, response 502` |
| `maven.google.com` | `curl: (35) schannel: handshake failed` |

A GitHub-hosted runner is not blocked: the CI job reaches the SDK repository, Google Maven (AGP,
AndroidX, WorkManager) and Maven Central, and has the SDK preinstalled. The `Android` workflow is
therefore the intended Stage C/D gate and will run `testDebugUnitTest lintDebug assembleDebug`
automatically once `src/android` exists.

## Install commands

Windows (once the package is published to `artifacts/windows`):

```powershell
# Place the supported Codex runtime beside the executable:
#   artifacts\windows\runtime\codex.exe   (the Codex CLI; app-server is its subcommand)
artifacts\windows\CodexQuota.Desktop.exe
```

Android (once an APK exists):

```powershell
adb install -r artifacts\android\app-debug.apk
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
