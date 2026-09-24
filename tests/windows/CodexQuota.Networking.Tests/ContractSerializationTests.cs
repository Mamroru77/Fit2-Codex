using System.Text.Json;
using System.Text.Json.Nodes;
using CodexQuota.Networking.Contracts.V1;

namespace CodexQuota.Networking.Tests;

/// <summary>
/// The v1 fixtures are the contract the Android client is written against. These tests pin the
/// wire shape: a round-trip through the DTOs must be byte-equivalent after normalisation, unknown
/// optional fields must be ignored rather than rejected, and a missing required field must be
/// reported as a protocol error instead of silently becoming zero.
/// </summary>
public class ContractSerializationTests
{
    private static readonly JsonSerializerOptions Options = V1Json.Options;

    [Theory]
    [InlineData("quota-v1.json")]
    [InlineData("quota-stale-v1.json")]
    public void QuotaFixtureRoundTripsThroughTheDto(string fileName)
    {
        var fixture = ContractFixtures.Load(fileName);

        var dto = JsonSerializer.Deserialize<QuotaResponse>(fixture, Options);
        Assert.NotNull(dto);

        var roundTripped = JsonSerializer.Serialize(dto, Options);

        Assert.Equal(ContractFixtures.Normalize(fixture), ContractFixtures.Normalize(roundTripped));
    }

    [Fact]
    public void QuotaFixtureExposesTheDocumentedWireNames()
    {
        var node = ContractFixtures.Parse("quota-v1.json");

        Assert.Equal(1, node["schemaVersion"]!.GetValue<int>());
        Assert.Equal("codex_app_server", node["source"]!.GetValue<string>());
        Assert.Equal("online", node["status"]!.GetValue<string>());

        var shortWindow = node["windows"]!["shortWindow"]!;
        Assert.Equal(28d, shortWindow["usedPercent"]!.GetValue<double>());
        Assert.Equal(72d, shortWindow["remainingPercent"]!.GetValue<double>());
        Assert.Equal(300, shortWindow["windowMinutes"]!.GetValue<int>());

        var weekly = node["windows"]!["weekly"]!;
        Assert.Equal(10080, weekly["windowMinutes"]!.GetValue<int>());
    }

    [Fact]
    public void AnUnknownOptionalFieldIsIgnored()
    {
        var node = ContractFixtures.Parse("quota-v1.json");
        node["windows"]!["shortWindow"]!["futureField"] = "ignored";
        node["anotherFutureField"] = 42;

        var json = node.ToJsonString(Options);

        // Adding optional fields is allowed within v1, so a client must not fail on them.
        var dto = JsonSerializer.Deserialize<QuotaResponse>(json, Options);

        Assert.NotNull(dto);
        Assert.Equal(72d, dto!.Windows.ShortWindow.RemainingPercent);
    }

    [Fact]
    public void AMissingRequiredFieldIsAProtocolErrorNotZero()
    {
        var node = ContractFixtures.Parse("quota-v1.json");
        node["windows"]!["shortWindow"]!.AsObject().Remove("remainingPercent");

        var error = V1ContractValidator.ValidateQuota(node);

        // Never silently default a missing percentage to 0: that would render as "no quota left".
        Assert.Equal(V1ContractValidator.DataProtocolError, error);
    }

    [Fact]
    public void ACompleteQuotaDocumentValidates()
    {
        Assert.Null(V1ContractValidator.ValidateQuota(ContractFixtures.Parse("quota-v1.json")));
        Assert.Null(V1ContractValidator.ValidateQuota(ContractFixtures.Parse("quota-stale-v1.json")));
    }

    [Theory]
    [InlineData("usedPercent")]
    [InlineData("windowMinutes")]
    [InlineData("resetsAt")]
    public void EveryRequiredWindowFieldIsValidated(string fieldName)
    {
        var node = ContractFixtures.Parse("quota-v1.json");
        node["windows"]!["weekly"]!.AsObject().Remove(fieldName);

        Assert.Equal(V1ContractValidator.DataProtocolError, V1ContractValidator.ValidateQuota(node));
    }

    [Fact]
    public void AnOutOfRangePercentageIsAProtocolError()
    {
        var node = ContractFixtures.Parse("quota-v1.json");
        node["windows"]!["shortWindow"]!["remainingPercent"] = 140;

        Assert.Equal(V1ContractValidator.DataProtocolError, V1ContractValidator.ValidateQuota(node));
    }

    [Fact]
    public void HistoryFixtureRoundTripsThroughTheDto()
    {
        var fixture = ContractFixtures.Load("history-v1.json");

        var dto = JsonSerializer.Deserialize<HistoryResponse>(fixture, Options);
        Assert.NotNull(dto);
        Assert.Equal(24, dto!.Hours);
        Assert.Equal(3, dto.Points.Count);
        Assert.Equal(81d, dto.Points[0].ShortWindowRemainingPercent);

        var roundTripped = JsonSerializer.Serialize(dto, Options);

        Assert.Equal(ContractFixtures.Normalize(fixture), ContractFixtures.Normalize(roundTripped));
    }

    [Fact]
    public void EventsFixtureRoundTripsThroughTheDto()
    {
        var fixture = ContractFixtures.Load("events-v1.json");

        var dto = JsonSerializer.Deserialize<EventsResponse>(fixture, Options);
        Assert.NotNull(dto);
        Assert.Equal(4, dto!.Events.Count);
        Assert.Equal("bridge_started", dto.Events[0].Type);
        Assert.Null(dto.Events[0].Detail);
        Assert.Equal("codex-cli 0.147.0", dto.Events[1].Detail);

        var roundTripped = JsonSerializer.Serialize(dto, Options);

        Assert.Equal(ContractFixtures.Normalize(fixture), ContractFixtures.Normalize(roundTripped));
    }

    [Fact]
    public void ErrorFixtureRoundTripsThroughTheDto()
    {
        var fixture = ContractFixtures.Load("error-auth-required-v1.json");

        var dto = JsonSerializer.Deserialize<ApiErrorResponse>(fixture, Options);
        Assert.NotNull(dto);
        Assert.Equal("CODEX_AUTH_REQUIRED", dto!.Error.Code);
        Assert.False(dto.Error.Retryable);

        var roundTripped = JsonSerializer.Serialize(dto, Options);

        Assert.Equal(ContractFixtures.Normalize(fixture), ContractFixtures.Normalize(roundTripped));
    }

    [Fact]
    public void WebSocketQuotaUpdateCarriesTypeSequenceAndTheFullSnapshot()
    {
        var fixture = ContractFixtures.Load("ws-quota-updated-v1.json");

        var dto = JsonSerializer.Deserialize<WsQuotaUpdatedMessage>(fixture, Options);
        Assert.NotNull(dto);
        Assert.Equal("quota.updated", dto!.Type);
        Assert.Equal(3L, dto.Sequence);

        // The complete normalised snapshot, not a delta.
        Assert.Equal(72d, dto.Payload.Windows.ShortWindow.RemainingPercent);
        Assert.Equal(54d, dto.Payload.Windows.Weekly.RemainingPercent);
        Assert.Equal(10080, dto.Payload.Windows.Weekly.WindowMinutes);

        var roundTripped = JsonSerializer.Serialize(dto, Options);

        Assert.Equal(ContractFixtures.Normalize(fixture), ContractFixtures.Normalize(roundTripped));
    }
}

/// <summary>Locates and normalises the committed contract fixtures.</summary>
internal static class ContractFixtures
{
    private static readonly string Root = FindRepositoryRoot();

    internal static string Load(string fileName)
        => File.ReadAllText(Path.Combine(Root, "contracts", "v1", fileName));

    internal static JsonObject Parse(string fileName)
        => JsonNode.Parse(Load(fileName))!.AsObject();

    /// <summary>
    /// Re-serialises through <see cref="JsonNode"/> so that property order and insignificant
    /// whitespace cannot make an otherwise identical document compare unequal.
    /// </summary>
    internal static string Normalize(string json)
    {
        var node = JsonNode.Parse(json);
        return Canonicalize(node).ToJsonString();
    }

    private static JsonNode? Canonicalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                var sorted = new JsonObject();
                foreach (var property in jsonObject.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    sorted[property.Key] = Canonicalize(property.Value);
                }

                return sorted;

            case JsonArray jsonArray:
                var items = new JsonArray();
                foreach (var item in jsonArray)
                {
                    items.Add(Canonicalize(item));
                }

                return items;

            // Numbers are compared by value, not by their source text: the fixtures spell a whole
            // number as `28.0` while the serializer writes `28`, and both mean the same thing.
            case JsonValue value when value.TryGetValue<double>(out var number):
                return JsonValue.Create(number);

            default:
                return node?.DeepClone();
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "contracts", "v1")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root (the directory holding contracts/v1) was not found.");
    }
}
