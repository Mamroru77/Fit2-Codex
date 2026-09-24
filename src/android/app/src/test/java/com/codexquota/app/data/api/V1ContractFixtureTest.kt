package com.codexquota.app.data.api

import com.codexquota.app.domain.QuotaSourceStatus
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject

/**
 * The Android side of the v1 contract.
 *
 * These tests read the same fixtures the Windows tests are proven against, which is what makes the
 * two implementations agree about the wire format rather than about two people's reading of it.
 */
class V1ContractFixtureTest {

    private fun fixture(name: String): String =
        checkNotNull(javaClass.getResourceAsStream("/$name")) {
            "Fixture $name is not on the test classpath; the contracts/v1 resource directory is not wired up."
        }.bufferedReader().use { it.readText() }

    @Test
    fun everyApprovedFixtureParses() {
        val quota = decodeV1<QuotaResponseDto>(fixture("quota-v1.json"), "quota-v1").toDomain()
        assertEquals(QuotaSourceStatus.Online, quota.status)
        assertEquals(1, quota.schemaVersion)
        assertEquals(72.0, quota.shortWindow.remainingPercent)
        assertEquals(54.0, quota.weekly.remainingPercent)
        assertEquals(300, quota.shortWindow.windowMinutes)
        assertEquals(10080, quota.weekly.windowMinutes)
        assertEquals(Instant.parse("2026-09-22T13:30:00Z"), quota.generatedAt)
        assertEquals(Instant.parse("2026-09-22T15:42:00Z"), quota.shortWindow.resetsAt)

        val stale = decodeV1<QuotaResponseDto>(fixture("quota-stale-v1.json"), "quota-stale-v1").toDomain()
        assertEquals(QuotaSourceStatus.Stale, stale.status)
        assertEquals(Instant.parse("2026-09-22T13:30:00Z"), stale.lastSuccessfulSyncAt)
        // A stale snapshot still carries the last trusted numbers rather than blanking them.
        assertEquals(72.0, stale.shortWindow.remainingPercent)

        val history = decodeV1<HistoryResponseDto>(fixture("history-v1.json"), "history-v1")
        assertEquals(24, history.hours)
        assertEquals(3, history.points.size)
        assertEquals(81.0, history.points.first().shortWindowRemainingPercent)

        val events = decodeV1<EventsResponseDto>(fixture("events-v1.json"), "events-v1")
        assertEquals(4, events.events.size)
        assertEquals(4, events.events.mapNotNull { it.toDomainOrNull() }.size)

        val error = decodeApiError(fixture("error-auth-required-v1.json"))
        assertEquals(ApiErrorCodes.CODEX_AUTH_REQUIRED, error.code)
        assertEquals(false, error.retryable)

        val frame = decodeV1<WsEnvelopeDto>(fixture("ws-quota-updated-v1.json"), "ws-quota-updated-v1")
        assertEquals("quota.updated", frame.type)
        assertEquals(3L, frame.sequence)
        assertEquals(72.0, assertNotNull(frame.payload).windows.shortWindow.remainingPercent)
    }

    @Test
    fun anUnknownOptionalFieldIsIgnored() {
        // This is the property that lets the Bridge add a field inside v1 without breaking a phone
        // that has not been updated yet.
        val mutated = withExtraField(fixture("quota-v1.json"), "futureUnknownField", JsonPrimitive("whatever"))

        val quota = decodeV1<QuotaResponseDto>(mutated, "quota with an unknown field").toDomain()

        assertEquals(72.0, quota.shortWindow.remainingPercent)
        assertEquals(54.0, quota.weekly.remainingPercent)
    }

    @Test
    fun aMissingRemainingPercentIsAProtocolErrorAndNeverZero() {
        // The whole point: a payload that never said how much quota is left must not be rendered as
        // "nothing left". It has to fail loudly, and the previous trusted cache must survive.
        val mutated = withoutField(fixture("quota-v1.json"), "windows.shortWindow.remainingPercent")

        val failure = assertFailsWith<DataProtocolException> {
            decodeV1<QuotaResponseDto>(mutated, "quota without remainingPercent").toDomain()
        }

        assertEquals(ApiErrorCodes.DATA_PROTOCOL_ERROR, failure.code)
    }

    @Test
    fun anOutOfRangePercentageIsAProtocolError() {
        // Replacing through the parsed document rather than through the text, so the test does not
        // depend on how the fixture happens to be spaced.
        val mutated = withField(
            fixture("quota-v1.json"),
            "windows.shortWindow.remainingPercent",
            JsonPrimitive(172.0),
        )

        assertFailsWith<DataProtocolException> {
            decodeV1<QuotaResponseDto>(mutated, "quota out of range").toDomain()
        }
    }

    @Test
    fun anUnknownSchemaVersionIsRefused() {
        val mutated = fixture("quota-v1.json").replace("\"schemaVersion\": 1", "\"schemaVersion\": 2")

        assertFailsWith<DataProtocolException> {
            decodeV1<QuotaResponseDto>(mutated, "quota with schema 2").toDomain()
        }
    }

    @Test
    fun anUnknownSourceStatusIsRefused() {
        val mutated = fixture("quota-v1.json").replace("\"status\": \"online\"", "\"status\": \"invented\"")

        assertFailsWith<DataProtocolException> {
            decodeV1<QuotaResponseDto>(mutated, "quota with an unknown status").toDomain()
        }
    }

    @Test
    fun technicalEventsAreFilteredOutOfTheUserTimeline() {
        val body = """
            { "hours": 24, "events": [
                { "type": "ping", "occurredAt": "2026-09-22T12:00:00Z", "detail": null },
                { "type": "http_success", "occurredAt": "2026-09-22T12:00:01Z", "detail": null },
                { "type": "window_reset", "occurredAt": "2026-09-22T15:42:00Z", "detail": "short window reset" }
            ] }
        """.trimIndent()

        val mapped = decodeV1<EventsResponseDto>(body, "events").events.mapNotNull { it.toDomainOrNull() }

        assertEquals(1, mapped.size)
        assertEquals("short window reset", mapped.single().detail)
    }

    @Test
    fun aPairingSessionMapsItsWireStatus() {
        val body = """
            { "pairingId": "abc", "status": "awaiting_local_approval", "verificationCode": "123456",
              "displayName": "OPPO Find X8", "expiresAt": "2026-09-22T13:35:00Z" }
        """.trimIndent()

        val session = decodeV1<PairingSessionDto>(body, "pairing session").toDomain()

        assertEquals(PairingWireStatus.AwaitingLocalApproval, session.status)
        assertEquals("123456", session.verificationCode)
        assertEquals(Instant.parse("2026-09-22T13:35:00Z"), session.expiresAt)
    }

    @Test
    fun aPairingStatusThisClientCannotReasonAboutIsRefused() {
        val body = """
            { "pairingId": "abc", "status": "something_new", "verificationCode": "123456",
              "expiresAt": "2026-09-22T13:35:00Z" }
        """.trimIndent()

        assertFailsWith<DataProtocolException> {
            decodeV1<PairingSessionDto>(body, "pairing session").toDomain()
        }
    }

    // --- helpers ------------------------------------------------------------------------------

    private fun withExtraField(body: String, path: String, value: JsonPrimitive): String =
        withField(body, path, value)

    /**
     * Sets one field, addressed by a dotted path.
     *
     * Only the innermost object is rebuilt, so the rest of the document is preserved exactly as the
     * fixture has it.
     */
    private fun withField(body: String, path: String, value: JsonPrimitive): String {
        val root = Json.parseToJsonElement(body).jsonObject
        val (head, _) = splitPath(path)
        val target = if (head.isEmpty()) root else descend(root, head)

        return Json.encodeToString(
            JsonObject.serializer(),
            JsonObject(target + (lastSegment(path) to value)),
        )
    }

    private fun withoutField(body: String, path: String): String {
        val root = Json.parseToJsonElement(body).jsonObject
        val (head, tail) = splitPath(path)
        val target = if (tail == null) root else descend(root, head)

        return Json.encodeToString(
            JsonObject.serializer(),
            JsonObject(target - lastSegment(path)),
        )
    }

    private fun splitPath(path: String): Pair<List<String>, String?> {
        val segments = path.split('.')
        return segments.dropLast(1) to segments.last()
    }

    private fun lastSegment(path: String): String = path.substringAfterLast('.')

    private fun descend(root: JsonObject, path: List<String>): JsonObject {
        var current = root
        for (segment in path) {
            current = current[segment]!!.jsonObject
        }
        return current
    }

    @Test
    fun theFixturesUsedByTheseTestsAreTheRepositoryOnes() {
        // Guards against a silently empty resource directory: every assertion above would otherwise
        // fail with a confusing message rather than naming the wiring problem.
        val quota = fixture("quota-v1.json")
        assertTrue(quota.contains("\"schemaVersion\""), "quota-v1.json did not look like a v1 document")
    }
}
