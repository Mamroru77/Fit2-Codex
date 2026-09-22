# Codex Quota Bridge V1 Implementation Roadmap

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the approved Codex Quota Bridge V1 as five independently reviewable stages, ending with the real Win11 → OPPO Find X8 → HUAWEI Health → WATCH FIT 2 validation chain.

**Architecture:** A Windows 11 Bridge owns Codex App Server and normalizes quota state, exposes a versioned HTTPS/WSS LAN protocol, and stores authoritative 24-hour history. A Kotlin/Compose Android app securely pairs to that Bridge, caches data, controls background/live synchronization, evaluates phone-local alert rules, and emits notifications mirrored to WATCH FIT 2.

**Tech Stack:** C#/.NET 8, ASP.NET Core/Kestrel, SQLite, WPF tray shell, Codex App Server JSONL/JSON-RPC; Kotlin, Jetpack Compose, Room, DataStore, OkHttp, WorkManager, Android foreground services; HTTPS/WSS, mDNS/DNS-SD, X.509/CNG identity.

**Spec:** `docs/superpowers/specs/2026-09-22-codex-quota-bridge-design.md`

## Global Constraints

- Windows 11 is the only Bridge target in V1; the Bridge is manual-start only and does not register auto-start.
- Android V1 is for the OPPO Find X8 class of device; live sync must use a `connectedDevice` foreground service, never an all-day `dataSync` foreground service.
- Low-power alert-only mode uses WorkManager periodic work with a 15-minute target interval and accepts inexact scheduling.
- WATCH FIT 2 has no native V1 app; all watch output comes from normal Android notifications through HUAWEI Health.
- Bridge API traffic is HTTPS/WSS only; no trust-all TLS, no hostname-verifier bypass, and no Bearer credential is sent before Bridge identity verification.
- Codex quota windows must be identified from explicit metadata; never hard-code `primary == shortWindow`.
- Current quota service remains usable when history persistence is unavailable.
- Android alert thresholds remain phone-local; the Bridge never stores or evaluates them.
- The visible history window is 24 hours; storage may retain roughly 25 hours for boundary margin.
- V1 has no cloud relay, remote tunnel, multi-account support, production multi-device UI, automatic updater, Android boot auto-start, or OPPO-private background hacks.
- Android LAN permission handling must be behind a `LocalNetworkPermissionController`; API 37+ can request `ACCESS_LOCAL_NETWORK` without changing networking code.

## Review Focus

1. **Quota schema drift:** an unknown/ambiguous Codex rate-limit layout must preserve the last trusted snapshot and surface `SOURCE_SCHEMA_UNSUPPORTED`, never guess values. Covered in Stage A adapter and integration tests.
2. **Identity-before-credential ordering:** on certificate/identity mismatch Android must stop before any device token is emitted. Covered in Stage B security integration tests and Stage C Android TLS tests.
3. **State restoration after process death:** Android must not repeat an already-fired critical alert after process recreation. Covered in Stage D alert-state persistence tests.
4. **LAN endpoint churn:** DHCP/interface changes must reconnect by stable Bridge identity without re-pairing. Covered in Stage B discovery tests and Stage E physical validation.
5. **Watch notification semantics:** updating an ongoing Android notification may or may not refresh WATCH FIT 2 content; test first and add a compatibility refresh only if the physical gate fails. Covered in Stage E.

---

## Repository layout locked by this roadmap

```text
contracts/
└─ v1/
   ├─ quota-v1.json
   ├─ quota-stale-v1.json
   ├─ history-v1.json
   ├─ events-v1.json
   ├─ error-auth-required-v1.json
   └─ ws-quota-updated-v1.json

src/
├─ windows/
│  ├─ CodexQuota.sln
│  ├─ CodexQuota.Core/
│  ├─ CodexQuota.Codex/
│  ├─ CodexQuota.Storage/
│  ├─ CodexQuota.Networking/
│  └─ CodexQuota.Desktop/
└─ android/
   ├─ settings.gradle.kts
   ├─ build.gradle.kts
   ├─ gradle.properties
   └─ app/
      └─ src/

tests/
└─ windows/
   ├─ CodexQuota.Core.Tests/
   ├─ CodexQuota.Codex.Tests/
   ├─ CodexQuota.Storage.Tests/
   ├─ CodexQuota.Networking.Tests/
   └─ CodexQuota.IntegrationTests/

docs/superpowers/
├─ specs/
└─ plans/
```

Android tests live under `src/android/app/src/test` and `src/android/app/src/androidTest` because that is the conventional Gradle layout.

## Stage order and hard gates

| Stage | Plan | Deliverable gate |
|---|---|---|
| A | `2026-09-22-stage-a-windows-quota-core.md` | Real Codex auth/quota works locally; history and tray state work; no LAN API yet |
| B | `2026-09-22-stage-b-lan-protocol-security.md` | HTTPS/WSS, Bridge identity, pairing, auth, mDNS and protocol contract are stable |
| C | `2026-09-22-stage-c-android-data-ui.md` | Phone securely pairs, shows live/cached quota/history, reconnects by state machine |
| D | `2026-09-22-stage-d-android-background-alerts.md` | OFF/BACKGROUND/LIVE modes and alert/notification semantics pass automated tests |
| E | `2026-09-22-stage-e-real-device-validation.md` | Actual Find X8 + HUAWEI Health + WATCH FIT 2 acceptance matrix passes |

Do not start a later stage until the previous stage's final verification command and manual gate pass.

## WorkBuddy execution policy

- Start each WorkBuddy session by giving it the approved spec, this roadmap, and exactly one stage plan.
- Ask the coding model to complete **one task at a time**, run the task's exact tests, commit, and stop for review.
- Prefer DeepSeek V4.1 Flash for routine implementation/refactors/tests and Hy4 Preview for protocol/security/state-machine reviews or when a task repeatedly fails.
- Never ask one agent turn to implement an entire stage.
- If a task needs to change a previously published interface, stop and update the plan first; do not silently drift the contract.

## Final verification after all stages

```powershell
# Windows
 dotnet test src/windows/CodexQuota.sln -c Release

# Android (from repo root on Windows)
 cd src/android
 .\gradlew.bat testDebugUnitTest lintDebug assembleDebug
```

Expected: every command exits 0. Physical Stage E validation is still required after automated tests pass.
