using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Serilog.Filters;

namespace Assistant.Logging;

/// <summary>
/// Cross-cutting log categories. A logger created with one of these names (or a sub-name such as
/// "Lab.Redis.Conversation") lands in the matching file, whatever service writes it.
/// </summary>
public static class LogCategories
{
    public const string Redis = "Lab.Redis";     // every Redis command we issue + connection events
    public const string Llm = "Lab.Llm";         // chat requests/replies, tool calls, embeddings, RAG searches
    public const string Clinic = "Lab.Clinic";   // the business system: Clinic.Api itself + the Worker's calls to it
    public const string Flow = "Lab.Flow";       // Worker diagnostics / end-to-end flow test
}

/// <summary>
/// One log folder for the whole lab (default: &lt;repo&gt;/logs, found by walking up to AssistantPlatform.sln):
/// <code>
///   all-YYYYMMDD.log       everything, from all services (column [Service])
///   worker-/gateway-/clinic-YYYYMMDD.log   one per service
///   redis-YYYYMMDD.log     Lab.Redis.* + StackExchange.Redis          (written by Gateway and Worker)
///   llm-YYYYMMDD.log       Lab.Llm.* + Microsoft.SemanticKernel.*     (Worker)
///   clinic-YYYYMMDD.log    Clinic.Api process + Worker's ClinicApiClient HTTP calls
/// </code>
/// Files are opened with shared: true because several processes write the same file (all-, redis-, clinic-).
/// Why files AND Phoenix traces: traces show one request's tree; files show the timeline across requests and
/// survive restarts — the first thing to grep when an incident happens.
/// </summary>
public static class LabLogging
{
    private const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{Service}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    private static readonly (string File, string[] Sources)[] CategoryFiles =
    [
        ("redis",  [LogCategories.Redis, "StackExchange.Redis"]),
        ("llm",    [LogCategories.Llm, "Microsoft.SemanticKernel"]),
        ("clinic", [LogCategories.Clinic, "System.Net.Http.HttpClient.ClinicApiClient"]),
    ];

    /// <param name="serviceName">worker | gateway | clinic — also the name of the per-service file.</param>
    public static IServiceCollection AddLabLogging(this IServiceCollection services, IConfiguration config,
        string serviceName, string contentRoot)
    {
        var dir = ResolveDirectory(config["LabLogging:Directory"], contentRoot);
        Directory.CreateDirectory(dir);
        services.AddSerilog((_, lc) => Configure(lc, config, serviceName, dir));
        return services;
    }

    public static LoggerConfiguration Configure(LoggerConfiguration lc, IConfiguration config, string service, string dir)
    {
        var level = Enum.TryParse<LogEventLevel>(config["LabLogging:MinimumLevel"], true, out var l) ? l : LogEventLevel.Information;
        var retain = int.TryParse(config["LabLogging:RetainDays"], out var days) ? days : 7;

        lc.MinimumLevel.Is(level)
          .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
          .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
          .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Information)  // "Request starting/finished"
          .MinimumLevel.Override("Microsoft.SemanticKernel", LogEventLevel.Information)                  // function invoking/succeeded
          .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Information)                // outgoing HTTP + status + ms
          .Enrich.FromLogContext()
          .Enrich.WithProperty("Service", service)
          .WriteTo.Console(outputTemplate: ConsoleTemplate)
          .WriteTo.File(Path.Combine(dir, "all-.log"), outputTemplate: FileTemplate,
              rollingInterval: RollingInterval.Day, retainedFileCountLimit: retain, shared: true)
          .WriteTo.File(Path.Combine(dir, $"{service}-.log"), outputTemplate: FileTemplate,
              rollingInterval: RollingInterval.Day, retainedFileCountLimit: retain, shared: true);

        foreach (var (file, sources) in CategoryFiles)
        {
            if (file == service) continue;            // Clinic.Api already writes clinic-.log as its service file
            var matchers = sources.Select(s => Matching.FromSource(s)).ToArray();
            lc.WriteTo.Logger(sub => sub
                .Filter.ByIncludingOnly(e => matchers.Any(m => m(e)))
                .WriteTo.File(Path.Combine(dir, $"{file}-.log"), outputTemplate: FileTemplate,
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: retain, shared: true));
        }
        return lc;
    }

    /// <summary>Configured path (absolute, or relative to the content root) → else &lt;repo&gt;/logs → else ./logs.</summary>
    public static string ResolveDirectory(string? configured, string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured));

        for (var d = new DirectoryInfo(contentRoot); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "AssistantPlatform.sln")))
                return Path.Combine(d.FullName, "logs");

        return Path.Combine(contentRoot, "logs");
    }
}
