# Stage B — LAN Protocol and Security Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the Stage A local Bridge into a securely pairable LAN service with stable Bridge identity, replaceable TLS leaf certificates, versioned REST/WSS contracts, device-token authentication, mDNS discovery, and safe endpoint/network handling.

**Architecture:** `CodexQuota.Networking` hosts Kestrel inside the existing desktop process. A long-lived CNG-backed Bridge CA identity signs short-lived leaf certificates; pairing bootstraps trust by QR/confirmation fingerprint before issuing a high-entropy device credential. REST/WSS read the same in-memory quota state and repositories already built in Stage A.

**Tech Stack:** .NET 8 ASP.NET Core/Kestrel, X.509 `CertificateRequest`, Windows CNG/CurrentUser key storage, `RandomNumberGenerator`, HMAC/fixed-time comparison, QRCoder, DNS-SD/mDNS library, xUnit + `Microsoft.AspNetCore.Mvc.Testing`/TestServer where useful.

**Spec:** `docs/superpowers/specs/2026-09-22-codex-quota-bridge-design.md`

## Global Constraints

- API base is `/api/v1`; application version, API version, and schema version remain independent.
- HTTPS/WSS only; no cleartext quota/history endpoint.
- Bridge identity is stable across DHCP/IP changes; leaf certificates may rotate without re-pairing.
- Pairing sessions expire after five minutes and are one-time use.
- Device credentials contain at least 256 bits of cryptographic entropy; Bridge persists only a hash.
- Unpaired/unauthorized devices cannot read quota/history/events or open the authenticated WebSocket.
- Do not send/accept Bearer credentials as part of pairing bootstrap before identity is verified.
- V1 UI permits one active phone, but storage stays multi-device-ready.
- mDNS advertises no personal data or secrets.
- Default listening scope is the selected Windows Private LAN interface, not public/VPN/Hyper-V/Docker adapters. V1 does not silently create a broad firewall rule; if Windows Firewall blocks inbound access, guide the user to allow only the Bridge executable/selected TCP port on the Private profile and never suggest disabling the firewall.

## Review Focus

1. A reused or expired pairing ID must fail even if every other field is valid.
2. Certificate renewal after IP change must preserve CA/Bridge fingerprint and device trust.
3. Revoked device tokens must immediately fail REST and WSS authorization.
4. WebSocket sequence gaps must be detectable; sequence must be monotonic across updates during one Bridge runtime.
5. A public/VPN/virtual adapter becoming available must not silently become the advertised/listening endpoint.

---

### Task 1: Create v1 contract fixtures and server DTOs

**Files:**
- Create: `contracts/v1/quota-v1.json`
- Create: `contracts/v1/quota-stale-v1.json`
- Create: `contracts/v1/history-v1.json`
- Create: `contracts/v1/events-v1.json`
- Create: `contracts/v1/error-auth-required-v1.json`
- Create: `contracts/v1/ws-quota-updated-v1.json`
- Create: `src/windows/CodexQuota.Networking/CodexQuota.Networking.csproj`
- Create: `src/windows/CodexQuota.Networking/Contracts/V1/QuotaResponse.cs`
- Create: `src/windows/CodexQuota.Networking/Contracts/V1/HistoryResponse.cs`
- Create: `src/windows/CodexQuota.Networking/Contracts/V1/EventsResponse.cs`
- Create: `src/windows/CodexQuota.Networking/Contracts/V1/ApiErrorResponse.cs`
- Create: `src/windows/CodexQuota.Networking/Contracts/V1/WsMessages.cs`
- Create: `tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj`
- Create: `tests/windows/CodexQuota.Networking.Tests/ContractSerializationTests.cs`

**Interfaces:**
- Produces public JSON property names exactly as approved by the spec.
- `QuotaResponse` contains `schemaVersion`, `generatedAt`, `source`, `status`, `lastSuccessfulSyncAt`, `windows.shortWindow`, `windows.weekly`.
- WebSocket quota update contains `type = "quota.updated"`, `sequence`, and full quota `payload`.

- [ ] **Step 1: Write fixture round-trip tests**

Deserialize each fixture into the corresponding DTO, serialize it back, and compare normalized `JsonNode` trees. Add a test that an additional unknown field in `quota-v1.json` deserializes successfully. Add a test that removing required `windows.shortWindow.remainingPercent` causes the explicit contract validator to return `DATA_PROTOCOL_ERROR` rather than defaulting to zero.

- [ ] **Step 2: Run contract tests and verify failure**

```powershell
 dotnet test tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj --filter ContractSerializationTests
```

Expected: FAIL.

- [ ] **Step 3: Implement DTOs, mappers, and validators**

Use `System.Text.Json` camel-case naming and explicit nullable/required handling. Keep domain-to-contract mapping in `Contracts/V1/V1ContractMapper.cs`; do not annotate domain records with wire-format attributes.

- [ ] **Step 4: Run contract tests**

```powershell
 dotnet test tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj --filter ContractSerializationTests
```

Expected: PASS.

- [ ] **Step 5: Commit protocol fixtures**

```powershell
 git add contracts src/windows/CodexQuota.Networking tests/windows/CodexQuota.Networking.Tests src/windows/CodexQuota.sln
 git commit -m "feat(protocol): lock v1 quota contract fixtures"
```

---

### Task 2: Implement stable Bridge identity and replaceable TLS leaf certificates

**Files:**
- Create: `src/windows/CodexQuota.Networking/Security/BridgeIdentity.cs`
- Create: `src/windows/CodexQuota.Networking/Security/IBridgeIdentityStore.cs`
- Create: `src/windows/CodexQuota.Networking/Security/WindowsCngBridgeIdentityStore.cs`
- Create: `src/windows/CodexQuota.Networking/Security/LeafCertificateFactory.cs`
- Create: `src/windows/CodexQuota.Networking/Security/CertificateFingerprint.cs`
- Create: `tests/windows/CodexQuota.Networking.Tests/BridgeIdentityTests.cs`
- Create: `tests/windows/CodexQuota.Networking.Tests/LeafCertificateFactoryTests.cs`

**Interfaces:**
- Produces: `Task<BridgeIdentity> GetOrCreateAsync(CancellationToken ct)`.
- Produces: `X509Certificate2 CreateServerCertificate(BridgeIdentity identity, IReadOnlyCollection<IPAddress> addresses, IReadOnlyCollection<string> dnsNames, DateTimeOffset now)`.
- Produces: `string ComputeSpkiSha256(X509Certificate2 certificate)`.
- Produces: `string ToHumanVerificationCode(string spkiSha256)` formatted as four groups of four uppercase hex characters from the fingerprint prefix, e.g. `A1B2-C3D4-E5F6-7890`.

- [ ] **Step 1: Write identity-persistence and IP-rotation tests**

Use a fake key store for unit tests. Assert repeated `GetOrCreateAsync` returns the same Bridge ID/fingerprint and same human verification code. Generate leaf A with `192.168.1.23`, leaf B with `192.168.1.87`; assert leaf thumbprints differ but both chains validate to the same Bridge identity public key and both expose the same pinned Bridge fingerprint.

- [ ] **Step 2: Run security tests and verify failure**

```powershell
 dotnet test tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj --filter "BridgeIdentityTests|LeafCertificateFactoryTests"
```

Expected: FAIL.

- [ ] **Step 3: Implement CNG-backed identity and leaf issuance**

Create a named RSA CNG key scoped to the current Windows user, generate a CA-capable self-signed identity cert, store only its public cert/metadata in normal app files, and keep private key material in the Windows key provider. Leaf cert validity: 90 days; renewal threshold: 30 days remaining or SAN set mismatch. Include `localhost`, the stable `codexquota-<shortBridgeId>.local` DNS name, and selected LAN IP SANs.

- [ ] **Step 4: Run tests and inspect a generated certificate**

```powershell
 dotnet test tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj --filter "BridgeIdentityTests|LeafCertificateFactoryTests"
```

Expected: PASS. Test output must show no private key PEM text.

- [ ] **Step 5: Commit Bridge identity**

```powershell
 git add src/windows/CodexQuota.Networking tests/windows/CodexQuota.Networking.Tests
 git commit -m "feat(security): add stable bridge identity and tls leaf rotation"
```

---

### Task 3: Implement pairing sessions and device credential storage

**Files:**
- Create: `src/windows/CodexQuota.Networking/Pairing/PairingSession.cs`
- Create: `src/windows/CodexQuota.Networking/Pairing/PairingService.cs`
- Create: `src/windows/CodexQuota.Networking/Pairing/DeviceCredential.cs`
- Create: `src/windows/CodexQuota.Networking/Auth/DeviceTokenService.cs`
- Create: `src/windows/CodexQuota.Storage/Devices/PairedDeviceRepository.cs`
- Create: `tests/windows/CodexQuota.Networking.Tests/PairingServiceTests.cs`
- Create: `tests/windows/CodexQuota.Networking.Tests/DeviceTokenServiceTests.cs`

**Interfaces:**
- Produces: `PairingSession CreateSession(PairingOrigin origin, string? requestedDisplayName, DateTimeOffset now)` with 5-minute expiry, random verification code, and state `AwaitingClient`/`AwaitingLocalApproval`/`Approved`/`Rejected`/`Consumed`/`Expired`.
- Produces: `PairingSession Claim(string pairingId, string displayName, DateTimeOffset now)` for QR-created sessions and `ApproveLocally(string pairingId)` / `RejectLocally(string pairingId)` for Windows tray actions.
- Produces: `PairingCompleteResult Complete(string pairingId, DateTimeOffset now)` only for an `Approved` session; completion atomically consumes it.
- Produces: `Task<DevicePrincipal?> ValidateAsync(string bearerToken, CancellationToken ct)`.
- Persisted device record stores `token_hash`, not token.

- [ ] **Step 1: Write expiry/reuse/entropy/revocation tests**

Assert an unapproved session cannot complete; a QR session can be claimed once; local reject prevents completion; local approval followed by completion at `expiresAt - 1s` succeeds; completion at `expiresAt` fails as expired; second completion fails as used. Assert the six-digit verification code is stable for the session but is not accepted as authentication, generated device token decodes to at least 32 random bytes, repository never receives plaintext token, and a revoked device fails validation. Use fixed-time hash comparison in implementation and test two wrong tokens of different prefixes both fail identically at the logical level.

- [ ] **Step 2: Run pairing/auth tests and verify failure**

```powershell
 dotnet test tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj --filter "PairingServiceTests|DeviceTokenServiceTests"
```

Expected: FAIL.

- [ ] **Step 3: Implement single-use pairing and high-entropy credentials**

Generate pairing IDs with `RandomNumberGenerator.GetBytes(16)` encoded base64url, a per-session six-digit human verification code with `RandomNumberGenerator.GetInt32(0, 1_000_000)`, and device credentials with 32 bytes encoded base64url. Hash credentials with SHA-256 before persistence. Keep live pairing sessions in memory only. Discovery requests enter `AwaitingLocalApproval`; locally-created QR sessions enter `AwaitingClient`, then `AwaitingLocalApproval` after claim. Only the Windows tray can transition a session to `Approved`; successful completion atomically marks it `Consumed` before returning a credential.

- [ ] **Step 4: Run pairing/auth tests**

```powershell
 dotnet test tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj --filter "PairingServiceTests|DeviceTokenServiceTests"
```

Expected: PASS.

- [ ] **Step 5: Commit pairing/auth**

```powershell
 git add src/windows/CodexQuota.Networking src/windows/CodexQuota.Storage tests/windows/CodexQuota.Networking.Tests
 git commit -m "feat(security): add one-time pairing and device credentials"
```

---

### Task 4: Host HTTPS Kestrel and device-auth middleware

**Files:**
- Create: `src/windows/CodexQuota.Networking/Hosting/BridgeApiHost.cs`
- Create: `src/windows/CodexQuota.Networking/Hosting/BridgeEndpointOptions.cs`
- Create: `src/windows/CodexQuota.Networking/Auth/DeviceAuthenticationMiddleware.cs`
- Create: `src/windows/CodexQuota.Networking/Auth/DeviceAuthExtensions.cs`
- Create: `tests/windows/CodexQuota.Networking.Tests/DeviceAuthenticationMiddlewareTests.cs`

**Interfaces:**
- Produces: embedded `WebApplication` bound to the selected private LAN address/port with the current leaf certificate.
- Pairing bootstrap routes are anonymous; `/health` may be anonymous but must disclose no quota/account secrets; all quota/history/events/WSS routes require a valid device principal.

- [ ] **Step 1: Write authorization boundary tests**

Test anonymous `/api/v1/quota` → 401 `DEVICE_UNAUTHORIZED`; valid bearer → reaches test endpoint; revoked bearer → 401; pairing route remains accessible without bearer; authentication middleware never logs raw `Authorization` content.

- [ ] **Step 2: Run middleware tests and verify failure**

```powershell
 dotnet test tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj --filter DeviceAuthenticationMiddlewareTests
```

Expected: FAIL.

- [ ] **Step 3: Implement embedded HTTPS host**

Configure Kestrel `Listen(address, port, listen => listen.UseHttps(leafCertificate))`. Do not call `ListenAnyIP`. Authenticate Bearer header through `DeviceTokenService`; attach a typed `DevicePrincipal` to `HttpContext.Items`. Return the standard v1 error envelope on failure.

- [ ] **Step 4: Run middleware tests**

```powershell
 dotnet test tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj --filter DeviceAuthenticationMiddlewareTests
```

Expected: PASS.

- [ ] **Step 5: Commit HTTPS/auth host**

```powershell
 git add src/windows/CodexQuota.Networking tests/windows/CodexQuota.Networking.Tests
 git commit -m "feat(protocol): host authenticated https bridge api"
```

---

### Task 5: Implement REST v1 endpoints and history bounds

**Files:**
- Create: `src/windows/CodexQuota.Networking/Api/V1/HealthEndpoints.cs`
- Create: `src/windows/CodexQuota.Networking/Api/V1/QuotaEndpoints.cs`
- Create: `src/windows/CodexQuota.Networking/Api/V1/HistoryEndpoints.cs`
- Create: `src/windows/CodexQuota.Networking/Api/V1/PairingEndpoints.cs`
- Create: `tests/windows/CodexQuota.Networking.Tests/RestApiTests.cs`

**Interfaces:**
- `GET /api/v1/health`
- `GET /api/v1/info`
- `GET /api/v1/quota`
- `GET /api/v1/history?hours=24`
- `GET /api/v1/events?hours=24`
- `POST /api/v1/pairing/request` for user-initiated discovery pairing; `POST /api/v1/pairing/claim` for a QR session created locally on Windows; `GET /api/v1/pairing/status/{pairingId}`; `POST /api/v1/pairing/complete`. No network route can approve a pairing session.

- [ ] **Step 1: Write REST endpoint tests**

Assert `/quota` reads the in-memory state store without invoking a mocked Codex client or SQLite repository. Assert `hours > 24`, `hours <= 0`, or non-numeric `hours` returns a stable validation error. Assert `auth_required` is returned as Bridge/source state rather than HTTP “computer offline”. Assert history repository unavailable returns a structured error while `/quota` still returns 200 with current snapshot.

- [ ] **Step 2: Run REST tests and verify failure**

```powershell
 dotnet test tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj --filter RestApiTests
```

Expected: FAIL.

- [ ] **Step 3: Implement endpoints with thin handlers**

Handlers call state/repository/service interfaces only. `pairing/request` may create a pending session only when the Android user explicitly taps Pair; it cannot approve or complete it. `pairing/claim` can only attach a phone display name to a still-valid locally-created QR session. Both flows expose the session verification code/status and require a Windows tray `Allow` action before `pairing/complete` can issue a device credential. Pairing QR payload returned to desktop UI contains `v`, `bridgeId`, `host`, `port`, `pairingId`, and `identityFingerprint`, never a device credential. Automatic-discovery pairing also requires Android to confirm the Bridge identity code before sending the request. Validate `hours` explicitly to 1–24.

- [ ] **Step 4: Run REST/contract tests**

```powershell
 dotnet test tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj --filter "RestApiTests|ContractSerializationTests"
```

Expected: PASS.

- [ ] **Step 5: Commit REST v1**

```powershell
 git add src/windows/CodexQuota.Networking tests/windows/CodexQuota.Networking.Tests contracts
 git commit -m "feat(protocol): add rest api v1 endpoints"
```

---

### Task 6: Implement authenticated WebSocket hub and sequence semantics

**Files:**
- Create: `src/windows/CodexQuota.Networking/WebSockets/QuotaWebSocketHub.cs`
- Create: `src/windows/CodexQuota.Networking/WebSockets/WebSocketConnection.cs`
- Create: `src/windows/CodexQuota.Networking/WebSockets/WebSocketMessageWriter.cs`
- Create: `tests/windows/CodexQuota.Networking.Tests/QuotaWebSocketHubTests.cs`

**Interfaces:**
- `wss://{paired-host}:{port}/api/v1/ws`
- First server frame: `hello` with `apiVersion` and `bridgeVersion`.
- State update: full snapshot plus `sequence` from `IQuotaStateStore`.
- Shutdown frame: `bridge.shutdown` before orderly host stop when possible.

- [ ] **Step 1: Write WebSocket message tests**

Test unauthorized upgrade rejection, hello-first ordering, sequence `1,2,3` on three state updates, complete snapshot payload (not delta), ping/pong heartbeat handling, and client cleanup after disconnect without leaking a subscriber.

- [ ] **Step 2: Run WebSocket tests and verify failure**

```powershell
 dotnet test tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj --filter QuotaWebSocketHubTests
```

Expected: FAIL.

- [ ] **Step 3: Implement hub with bounded per-client send channel**

Never write to one WebSocket from multiple threads. Each connection owns one send loop and bounded `Channel<string>`; quota broadcast enqueues a complete serialized message. If a client cannot keep up and the channel fills, close it rather than blocking Bridge state updates.

- [ ] **Step 4: Run WebSocket tests**

```powershell
 dotnet test tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj --filter QuotaWebSocketHubTests
```

Expected: PASS.

- [ ] **Step 5: Commit WSS**

```powershell
 git add src/windows/CodexQuota.Networking tests/windows/CodexQuota.Networking.Tests
 git commit -m "feat(protocol): stream quota updates over authenticated websocket"
```

---

### Task 7: Add private-LAN endpoint selection, certificate renewal, mDNS and QR UI

**Files:**
- Create: `src/windows/CodexQuota.Networking/Discovery/PrivateLanInterfaceSelector.cs`
- Create: `src/windows/CodexQuota.Networking/Discovery/MdnsPublisher.cs`
- Create: `src/windows/CodexQuota.Networking/Hosting/EndpointLifecycleCoordinator.cs`
- Create: `src/windows/CodexQuota.Desktop/Views/PairingQrWindow.xaml`
- Create: `src/windows/CodexQuota.Desktop/ViewModels/PairingViewModel.cs`
- Create: `tests/windows/CodexQuota.Networking.Tests/PrivateLanInterfaceSelectorTests.cs`
- Create: `tests/windows/CodexQuota.Networking.Tests/EndpointLifecycleCoordinatorTests.cs`

**Interfaces:**
- Select one configured/default private IPv4 interface.
- Reject loopback, public-profile interfaces, VPN/tunnel, Hyper-V/Docker-style virtual interfaces by policy metadata/configuration.
- Advertise `_codexquota._tcp.local.` with only `bridgeId`, `apiVersion`, `tls=1` and port.
- Surface a typed `FirewallBlockedOrUnreachable` diagnostic when the selected private endpoint is healthy locally but a physical LAN smoke test cannot reach it; documentation must recommend a Private-profile scoped allow rule, not firewall disablement.

- [ ] **Step 1: Write endpoint selection/change tests**

Feed synthetic interface descriptors containing Ethernet/private, Wi-Fi/private, VPN, Docker, Hyper-V, public Wi-Fi, and loopback. Assert only eligible private adapters are candidates and explicit user selection wins. Simulate IP `192.168.1.23 → 192.168.1.87` and assert endpoint coordinator renews leaf SANs and re-publishes mDNS while Bridge identity fingerprint remains unchanged.

- [ ] **Step 2: Run discovery tests and verify failure**

```powershell
 dotnet test tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj --filter "PrivateLanInterfaceSelectorTests|EndpointLifecycleCoordinatorTests"
```

Expected: FAIL.

- [ ] **Step 3: Implement discovery lifecycle and QR window**

Pin the LAN discovery and QR packages used by the .NET 8 Bridge:

```powershell
 dotnet add src/windows/CodexQuota.Networking/CodexQuota.Networking.csproj package Systola.Makaretu.Dns.Multicast --version 2026.3.12
 dotnet add src/windows/CodexQuota.Desktop/CodexQuota.Desktop.csproj package QRCoder.Core --version 2.0.2
```

Subscribe to `NetworkChange.NetworkAddressChanged`, debounce changes for 500 ms, re-evaluate the endpoint, renew leaf when SANs changed, restart only the Kestrel listener if necessary, and update mDNS. Use QRCoder to render the exact pairing JSON returned by `PairingService`; no long-lived secret/token is embedded. The pairing window displays the same `A1B2-C3D4-E5F6-7890`-style Bridge identity code Android derives from the presented identity. When a discovery request or QR claim arrives, show phone display name plus the session's six-digit verification code and explicit `Allow` / `Reject` actions; only `Allow` calls `ApproveLocally`.

- [ ] **Step 4: Run discovery tests and manual mDNS smoke check**

```powershell
 dotnet test tests/windows/CodexQuota.Networking.Tests/CodexQuota.Networking.Tests.csproj --filter "PrivateLanInterfaceSelectorTests|EndpointLifecycleCoordinatorTests"
```

Expected: PASS. Manual: a Bonjour/DNS-SD browser on the LAN should see one `_codexquota._tcp` record and no account/username/token TXT fields.

- [ ] **Step 5: Commit discovery/QR**

```powershell
 git add src/windows/CodexQuota.Networking src/windows/CodexQuota.Desktop tests/windows/CodexQuota.Networking.Tests
 git commit -m "feat(protocol): add lan discovery endpoint rotation and pairing qr"
```

---

### Task 8: Stage B security/integration gate and orderly shutdown

**Files:**
- Create: `tests/windows/CodexQuota.IntegrationTests/StageBSecurityIntegrationTests.cs`
- Modify: `src/windows/CodexQuota.Desktop/Runtime/BridgeHostedService.cs`
- Modify: `src/windows/CodexQuota.Desktop/Tray/TrayController.cs`

**Interfaces:**
- Shutdown order: stop pairing → send `bridge.shutdown` → stop mDNS → stop HTTPS/WSS → flush persistence → stop Codex child → dispose/exit.

- [ ] **Step 1: Write full security-path integration tests**

Cover: anonymous quota rejected; discovery pairing cannot complete before local approval; QR claim cannot complete before local approval; rejected pairing cannot complete; approved fresh pairing succeeds; reused pairing rejected; expired pairing rejected; correct token reads quota; revoked token rejected; WebSocket auth works; identity A leaf rotates and client remains valid; identity B replacement must fail the test client's pin before it sends the Authorization header. Instrument the fake client to record whether credentials were emitted.

- [ ] **Step 2: Run integration tests and verify failure before final wiring**

```powershell
 dotnet test tests/windows/CodexQuota.IntegrationTests/CodexQuota.IntegrationTests.csproj --filter StageBSecurityIntegrationTests
```

Expected: FAIL until all host lifecycle wiring is complete.

- [ ] **Step 3: Wire networking into the desktop host and shutdown pipeline**

Start API only after identity/endpoint are ready. Expose tray `Pair Device`. On exit disable new pairing first, broadcast shutdown with a short bounded timeout, stop mDNS/API, await persistence drain, then stop Codex child. Add paired-device reset/revoke action to Settings.

- [ ] **Step 4: Run Stage A+B full verification**

```powershell
 dotnet test src/windows/CodexQuota.sln -c Release
```

Expected: PASS.

Manual LAN smoke gate from a second machine/phone browser that can inspect certificates: HTTPS presents the generated leaf; HTTP cleartext port is absent; unpaired quota request cannot retrieve data.

- [ ] **Step 5: Commit and tag Stage B**

```powershell
 git add src/windows tests/windows contracts
 git commit -m "feat(protocol): complete secure lan bridge protocol"
 git tag stage-b-lan-security
```
