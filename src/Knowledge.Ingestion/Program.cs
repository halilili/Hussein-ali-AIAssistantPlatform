using Assistant.Core;
using Assistant.Core.Knowledge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Assistant.Core.Llm;

// Knowledge ingestion: markdown files → chunks → bge-m3 embeddings → Qdrant (tenant-scoped).
// Usage (from the repo root):
//   dotnet run --project src/Knowledge.Ingestion -- --tenant clinic-haddad --path knowledge/clinic-haddad
//   dotnet run --project src/Knowledge.Ingestion -- --tenant clinic-haddad --ask "do you accept insurance?"
// Defaults in LlmOptions/KnowledgeOptions target the local docker stack; override with --Llm:Endpoint=... etc.
//
// NOTE: --tenant / --path / --ask are read from the COMMAND LINE ONLY, never from IConfiguration.
// IConfiguration also loads environment variables, and on Windows the PATH variable would be read as "path"
// (keys are case-insensitive) when --path is not given.
// Windows consoles default to an OEM code page and print Arabic as "????". Force UTF-8 output.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var tenant = Arg(args, "tenant") ?? throw new ArgumentException("--tenant is required, e.g. --tenant clinic-haddad");
var path = Arg(args, "path");
var question = Arg(args, "ask");
if (path is null && question is null)
    throw new ArgumentException("Give --path <folder> to ingest and/or --ask \"question\" to test retrieval.");

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAssistantKnowledge(builder.Configuration);
using var host = builder.Build();

var kopt = host.Services.GetRequiredService<IOptions<KnowledgeOptions>>().Value;
Console.WriteLine($"Settings: collection={kopt.Collection}, TopK={kopt.TopK}, MinScore={kopt.MinScore}   (override e.g. --Knowledge:MinScore 0)");

var index = host.Services.GetRequiredService<KnowledgeIndex>();
await index.EnsureCollectionAsync();

if (path is not null)
{
    if (!Directory.Exists(path))
        throw new DirectoryNotFoundException($"Folder '{Path.GetFullPath(path)}' not found. Run from the solution root or pass an absolute path.");

    var total = 0;
    foreach (var file in Directory.EnumerateFiles(path, "*.md").Order())
    {
        var source = Path.GetFileName(file);
        var chunks = MarkdownChunker.Split(await File.ReadAllTextAsync(file));
        var count = await index.ReplaceDocumentAsync(tenant, source, chunks);
        Console.WriteLine($"{source,-28} {count,3} chunks");
        total += count;
    }
    Console.WriteLine($"Done: {total} chunks indexed for tenant '{tenant}'.");
}

if (question is not null)
{
    Console.WriteLine($"\nQ: {question}");
    // Diagnostic: if the argument itself was corrupted, it arrives as literal '?' characters (U+003F).
    var questionMarks = question.Count(c => c == '?');
    Console.WriteLine($"   ({question.Length} chars, {questionMarks} '?' — if almost all are '?', the argument was corrupted before reaching the program)");
    var hits = await index.SearchAsync(tenant, question);
    if (hits.Count == 0) Console.WriteLine("  (no chunk above the score threshold — did you ingest this tenant first?)");
    foreach (var hit in hits)
        Console.WriteLine($"  {hit.Score:0.000}  {hit.Source,-26} {hit.Heading}");
}

static string? Arg(string[] args, string name)
{
    var i = Array.FindIndex(args, a => string.Equals(a, "--" + name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
