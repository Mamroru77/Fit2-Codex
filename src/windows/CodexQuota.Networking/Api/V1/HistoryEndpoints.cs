using System.Globalization;
using CodexQuota.Networking.Contracts.V1;
using CodexQuota.Storage.History;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CodexQuota.Networking.Api.V1;

/// <summary>
/// The 24-hour history and event timeline.
/// </summary>
public static class HistoryEndpoints
{
    /// <summary>Default window when the client does not ask for one.</summary>
    public const int DefaultHours = 24;

    /// <summary>Smallest window the Bridge will serve.</summary>
    public const int MinimumHours = 1;

    /// <summary>
    /// Largest window the Bridge will serve. It is exactly the visible history promise, so a client
    /// cannot ask for data the Bridge deliberately does not keep.
    /// </summary>
    public const int MaximumHours = 24;

    /// <summary>Maps <c>GET /api/v1/history</c> and <c>GET /api/v1/events</c>.</summary>
    public static void MapHistoryEndpoints(this WebApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        application.MapGet(
            "/api/v1/history",
            (HttpRequest request, IHistoryRepository repository) => HandleHistoryAsync(request, repository));

        application.MapGet(
            "/api/v1/events",
            (HttpRequest request, IHistoryRepository repository) => HandleEventsAsync(request, repository));
    }

    private static async Task<IResult> HandleHistoryAsync(HttpRequest request, IHistoryRepository repository)
    {
        if (!TryReadHours(request, out var hours))
        {
            return OutOfBounds();
        }

        var to = DateTimeOffset.UtcNow;
        var from = to.AddHours(-hours);

        try
        {
            var points = await repository
                .ReadHistoryAsync(from, to, request.HttpContext.RequestAborted)
                .ConfigureAwait(false);

            return Results.Ok(V1ContractMapper.ToHistoryResponse(hours, points));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // History is a secondary concern. Losing it must be reported as such, and must never
            // take the current quota snapshot down with it.
            return ApiResults.Unavailable(
                ApiErrorCodes.HistoryUnavailable,
                "The Bridge history is currently unavailable.");
        }
    }

    private static async Task<IResult> HandleEventsAsync(HttpRequest request, IHistoryRepository repository)
    {
        if (!TryReadHours(request, out var hours))
        {
            return OutOfBounds();
        }

        var to = DateTimeOffset.UtcNow;
        var from = to.AddHours(-hours);

        try
        {
            var events = await repository
                .ReadEventsAsync(from, to, request.HttpContext.RequestAborted)
                .ConfigureAwait(false);

            return Results.Ok(V1ContractMapper.ToEventsResponse(hours, events));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return ApiResults.Unavailable(
                ApiErrorCodes.HistoryUnavailable,
                "The Bridge history is currently unavailable.");
        }
    }

    private static IResult OutOfBounds()
        => ApiResults.Invalid($"hours must be a whole number between {MinimumHours} and {MaximumHours}.");

    /// <summary>
    /// Reads the <c>hours</c> parameter. A missing parameter takes the default; anything present
    /// must parse and be in range, so a client cannot silently get a different window than it asked
    /// for.
    /// </summary>
    private static bool TryReadHours(HttpRequest request, out int hours)
    {
        hours = DefaultHours;

        if (!request.Query.TryGetValue("hours", out var values))
        {
            return true;
        }

        var raw = values.ToString();

        if (string.IsNullOrWhiteSpace(raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < MinimumHours
            || parsed > MaximumHours)
        {
            return false;
        }

        hours = parsed;
        return true;
    }
}
