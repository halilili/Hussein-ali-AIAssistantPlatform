using System.Diagnostics;
using Assistant.Logging;
using Microsoft.SemanticKernel;

namespace Agent.Worker.Agents;

/// <summary>
/// Semantic Kernel filter around EVERY tool call the model makes: arguments in, result out, duration.
/// SK's own log only says "Function clinic-propose_booking succeeded"; this shows WHAT was asked and answered —
/// the evidence you need when the model claims something the tool never returned (HARD RULE 7 incident).
/// Registered in DI; kernels created by AddKernel() pick up IFunctionInvocationFilter services automatically.
/// </summary>
public sealed class ToolCallLoggingFilter(ILoggerFactory loggers) : IFunctionInvocationFilter
{
    private const int MaxResultChars = 1500;
    private readonly ILogger _log = loggers.CreateLogger(LogCategories.Llm + ".Tools");

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        var name = $"{context.Function.PluginName}-{context.Function.Name}";
        var args = string.Join(", ", context.Arguments.Select(a => $"{a.Key}={a.Value}"));
        var start = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
            var result = context.Result.ToString() ?? "";
            if (result.Length > MaxResultChars) result = result[..MaxResultChars] + "…";
            _log.LogInformation("TOOL {Tool}({Args}) → {Result} ({Ms:0} ms)",
                name, args, result, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TOOL {Tool}({Args}) THREW after {Ms:0} ms — the action did NOT happen",
                name, args, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            throw;
        }
    }
}
