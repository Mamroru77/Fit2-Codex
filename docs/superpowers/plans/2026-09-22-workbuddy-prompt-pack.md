# WorkBuddy Prompt Pack — Codex Quota Bridge V1

Use these prompts after the corresponding plan has been reviewed. Do not give WorkBuddy all implementation stages at once.

## Session bootstrap prompt

```text
You are implementing an approved project specification using strict TDD and small commits.

Read these files first, in order:
1. docs/superpowers/specs/2026-09-22-codex-quota-bridge-design.md
2. docs/superpowers/plans/2026-09-22-codex-quota-roadmap.md
3. <CURRENT_STAGE_PLAN_PATH>

Rules:
- Implement exactly ONE numbered task from the current stage plan, then stop.
- Follow every checkbox step in that task in order: failing test → verify failure → minimal implementation → verify pass → commit.
- Do not implement later tasks early.
- Do not change approved public interfaces unless a test proves the plan is impossible; if that happens, stop and explain the mismatch instead of improvising.
- Never use TrustAllCertificates, hostname-verifier bypass, plaintext long-lived tokens, hard-coded LAN IPs, primary==shortWindow assumptions, while(true) quota polling, OPPO-private background hacks, or Android boot auto-start.
- Preserve the last trusted quota when source/protocol data is invalid; never fabricate 0% or 100%.
- Show the exact test command and result before claiming the task is complete.
- At the end, show `git status --short` and the commit hash, then stop for review.

Current task: <TASK_NUMBER_AND_NAME>
```

## Stage A prompt

```text
Use the session bootstrap rules.
CURRENT_STAGE_PLAN_PATH = docs/superpowers/plans/2026-09-22-stage-a-windows-quota-core.md
Implement only the next uncompleted task in Stage A.
Pay special attention to Codex App Server JSONL initialization, schema-safe 300/10080-minute window mapping, last-trusted-state behavior, bounded child restart, and history failure isolation.
Do not create LAN REST/WebSocket endpoints yet.
```

## Stage B prompt

```text
Use the session bootstrap rules.
CURRENT_STAGE_PLAN_PATH = docs/superpowers/plans/2026-09-22-stage-b-lan-protocol-security.md
Implement only the next uncompleted task in Stage B.
Stage A interfaces are considered stable.
Security invariants: verify Bridge identity before any long-lived device Bearer credential can be sent; no network endpoint can approve a pairing; discovery/QR pairing must wait for explicit Windows-tray Allow before completion. Pairing sessions are five-minute, one-use; the Bridge stores only hashes of 256-bit device credentials.
Do not weaken TLS for tests; use test certificates/fakes instead.
```

## Stage C prompt

```text
Use the session bootstrap rules.
CURRENT_STAGE_PLAN_PATH = docs/superpowers/plans/2026-09-22-stage-c-android-data-ui.md
Implement only the next uncompleted task in Stage C.
Stage B /api/v1 and WSS contracts are stable and represented by contracts/v1 fixtures.
Android must ignore unknown optional JSON fields but reject missing required quota fields without turning them into zero.
Pairing bootstrap verifies the stable Bridge identity CA/fingerprint (not the rotating leaf SPKI), preserves normal hostname/SAN validation, and waits for Windows-local approval before completing.
Compose/ViewModels must not own sockets.
Do not implement foreground-service/background-alert scheduling yet; that belongs to Stage D.
```

## Stage D prompt

```text
Use the session bootstrap rules.
CURRENT_STAGE_PLAN_PATH = docs/superpowers/plans/2026-09-22-stage-d-android-background-alerts.md
Implement only the next uncompleted task in Stage D.
Live mode must use foregroundServiceType=connectedDevice, never all-day dataSync.
Live FGS and periodic WorkManager must be mutually exclusive.
Alert evaluation must remain deterministic and side-effect free; notification delivery is separate.
Do not add BOOT_COMPLETED auto-start or OPPO-private APIs.
```

## Stage E prompt

```text
Use the session bootstrap rules.
CURRENT_STAGE_PLAN_PATH = docs/superpowers/plans/2026-09-22-stage-e-real-device-validation.md
Execute only the next uncompleted validation task.
Do not invent a WATCH FIT 2 refresh workaround before the physical ongoing-notification gate fails with evidence.
Security failures must never be bypassed to make a test pass.
Record exact device/software versions and PASS/FAIL evidence in docs/validation/v1-device-validation.md.
```

## Review prompt after each task

Use Hy4 Preview (or the stronger reviewing model available) with:

```text
Review the most recent commit against:
- docs/superpowers/specs/2026-09-22-codex-quota-bridge-design.md
- the exact task just completed in the current stage plan.

Do not rewrite the implementation. Review it.
Check, in priority order:
1. security/correctness violations;
2. whether tests actually prove the required behavior;
3. interface drift from the plan;
4. failure isolation and state-machine edge cases;
5. needless complexity/YAGNI.

For every issue, cite the file and line/range, explain the concrete failure mode, and classify it as Blocker / Important / Minor.
If there are no Blocker or Important findings, say that explicitly.
```

## Fix prompt when review finds issues

```text
Read the reviewer findings and the approved spec/task.
Address only Blocker and Important findings first.
For each finding:
1. reproduce it with a failing test where feasible;
2. show the failing test output;
3. make the smallest fix consistent with the approved architecture;
4. run the focused test, then the task's full verification command;
5. commit with a message describing the fix.
Do not opportunistically refactor unrelated code.
```

## Stage completion prompt

```text
The current stage's numbered tasks are complete.
Run the stage's final verification command from the plan and compare every Stage Definition of Done item in the spec against concrete evidence.
Output a checklist with test command/result or manual evidence for each item.
Do not begin the next stage.
If any item lacks evidence, mark the stage NOT COMPLETE and identify the exact next action.
```

## Model allocation recommendation

- **DeepSeek V4.1 Flash:** routine TDD tasks, DTOs, persistence, Compose screens, straightforward WorkManager/service wiring, test repairs with clear failure output.
- **Hy4 Preview:** RateLimitAdapter ambiguity review, JSON-RPC lifecycle, TLS/identity/pairing, connection state machine, alert state restoration, Stage B/D completion review, any task that fails twice without a clear local cause.

The model allocation is a workflow preference, not a correctness dependency; the plan/tests remain the source of truth.
