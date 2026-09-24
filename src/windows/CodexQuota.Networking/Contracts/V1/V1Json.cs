using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuota.Networking.Contracts.V1;

/// <summary>
/// Shared serialization settings for the v1 wire contract.
/// </summary>
/// <remarks>
/// Property names are camel-cased and timestamps are always UTC in the <c>Z</c> form the approved
/// spec uses. The default <see cref="JsonSerializer"/> behaviour would emit <c>+00:00</c> instead,
/// which is the same instant but not the same document.
/// </remarks>
public static class V1Json
{
    /// <summary>The v1 serializer settings. Reused by every contract test and endpoint.</summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        options.Converters.Add(new UtcDateTimeOffsetConverter());

        return options;
    }
}

/// <summary>
/// Reads and writes <see cref="DateTimeOffset"/> as a UTC instant with a trailing <c>Z</c>.
/// </summary>
/// <remarks>
/// The fractional part is emitted only when it is non-zero, and to the precision actually present,
/// so a whole-second timestamp round-trips as <c>2026-09-22T13:30:00Z</c> exactly as the contract
/// fixtures show it.
/// </remarks>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const string SecondPrecision = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const string MillisecondPrecision = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string TickPrecision = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTimeOffset().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(Format(value));

    internal static string Format(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();

        var format = utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? SecondPrecision
            : utc.Ticks % TimeSpan.TicksPerMillisecond == 0
                ? MillisecondPrecision
                : TickPrecision;

        return utc.ToString(format, CultureInfo.InvariantCulture);
    }
}
