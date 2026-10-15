using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Agent.Worker.Clients;
using Assistant.Core.Tenants;

namespace Agent.Worker.Prompts;

public sealed record RenderedPrompt(string Text, string Version, string TemplateHash);

/// <summary>
/// System prompts are versioned files (prompts/{vertical}/system.{version}.md), reviewed in pull requests
/// like code. The tenant row selects the version → canary a new prompt on one clinic, roll back by editing one column.
/// Rendering is plain {{placeholder}} replacement: deterministic, testable, and it FAILS FAST on a missing value
/// instead of silently sending "{{address}}" to the model.
/// </summary>
public sealed class PromptLibrary
{
    private static readonly Regex Placeholder = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, (string Template, string Hash)> _templates = new();

    public RenderedPrompt RenderClinicSystemPrompt(Tenant tenant, IReadOnlyList<ClinicDoctor> doctors, DateTimeOffset nowUtc)
    {
        var (template, hash) = Load(tenant.Vertical, tenant.PromptVersion);
        var local = TimeZoneInfo.ConvertTime(nowUtc, tenant.TimeZone);

        var values = new Dictionary<string, string>
        {
            ["business_name"] = tenant.DisplayName,
            ["address"] = tenant.Setting("address"),
            ["clinic_phone"] = tenant.Setting("clinic_phone"),
            ["emergency_number"] = tenant.Setting("emergency_number"),
            ["languages"] = string.Join(", ", tenant.Languages),
            ["timezone"] = tenant.TimeZoneId,
            ["doctors"] = string.Join('\n', doctors.Select(d =>
                $"- id={d.Id} | {d.FullName} | {d.Specialty} | {d.SlotMinutes} min | consultation {d.ConsultationFeeUsd.ToString("0", CultureInfo.InvariantCulture)} USD")),
            ["now_local"] = local.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["calendar"] = BuildCalendar(DateOnly.FromDateTime(local.DateTime), days: 14)
        };

        var text = Placeholder.Replace(template, m =>
            values.TryGetValue(m.Groups[1].Value, out var v)
                ? v
                : throw new InvalidOperationException($"Prompt {tenant.Vertical}/system.{tenant.PromptVersion}: no value for {m.Value}"));

        return new RenderedPrompt(text, tenant.PromptVersion, hash);
    }

    private (string Template, string Hash) Load(string vertical, string version) =>
        _templates.GetOrAdd($"{vertical}/system.{version}.md", key =>
        {
            var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "prompts", key));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
            return (text, hash);
        });

    /// <summary>
    /// Small models are unreliable at date arithmetic ("next Tuesday" from a Wednesday). Giving them a
    /// lookup table turns a reasoning problem into a copy problem.
    /// </summary>
    private static string BuildCalendar(DateOnly today, int days)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < days; i++)
        {
            var d = today.AddDays(i);
            var label = i switch { 0 => " (today)", 1 => " (tomorrow / bukra / demain)", _ => "" };
            sb.Append("- ").Append(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
              .Append(' ').Append(d.DayOfWeek).Append(label).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }
}
