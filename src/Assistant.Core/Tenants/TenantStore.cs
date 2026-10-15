using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Assistant.Core.Tenants;

/// <summary>A business subscribed to the platform (clinic, restaurant, pharmacy).</summary>
public sealed record Tenant(
    string Id,
    string DisplayName,
    string Vertical,
    string? WaPhoneNumberId,
    string TimeZoneId,
    string[] Languages,
    string PromptVersion,
    string SettingsJson)
{
    private JsonElement? _settings;

    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    /// <summary>Reads a string value from the tenant's settings JSON; fails fast when a required key is missing.</summary>
    public string Setting(string key)
    {
        _settings ??= JsonDocument.Parse(SettingsJson).RootElement.Clone();
        return _settings.Value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new InvalidOperationException($"Tenant '{Id}' is missing setting '{key}'.");
    }
}

/// <summary>
/// Read-through cache over platform.tenants. Tenants change rarely; every inbound message needs one,
/// so a 5-minute in-memory cache removes a DB round-trip from the hot path.
/// </summary>
public sealed class TenantStore(NpgsqlDataSource db, IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    // Column order MUST match the Tenant record constructor (Dapper positional mapping).
    private const string SelectColumns = """
        SELECT id AS Id, display_name AS DisplayName, vertical AS Vertical,
               wa_phone_number_id AS WaPhoneNumberId, timezone AS TimeZoneId,
               languages AS Languages, prompt_version AS PromptVersion, settings::text AS SettingsJson
        FROM platform.tenants
        """;

    public Task<Tenant?> GetAsync(string tenantId, CancellationToken ct = default) =>
        cache.GetOrCreateAsync($"tenant:id:{tenantId}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = Ttl;
            await using var c = await db.OpenConnectionAsync(ct);
            return await c.QuerySingleOrDefaultAsync<Tenant>(
                new CommandDefinition($"{SelectColumns} WHERE id = @tenantId AND is_active", new { tenantId }, cancellationToken: ct));
        });

    public Task<Tenant?> FindByWhatsAppNumberAsync(string phoneNumberId, CancellationToken ct = default) =>
        cache.GetOrCreateAsync($"tenant:wa:{phoneNumberId}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = Ttl;
            await using var c = await db.OpenConnectionAsync(ct);
            return await c.QuerySingleOrDefaultAsync<Tenant>(
                new CommandDefinition($"{SelectColumns} WHERE wa_phone_number_id = @phoneNumberId AND is_active", new { phoneNumberId }, cancellationToken: ct));
        });
}
