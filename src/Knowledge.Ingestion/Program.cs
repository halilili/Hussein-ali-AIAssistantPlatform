using Assistant.Core;
using Assistant.Core.Knowledge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Knowledge ingestion: markdown files → chunks → bge-m3 embeddings → Qdrant (tenant-scoped).
// Usage (from the repo root):
//   dotnet run --project src/Knowledge.Ingestion -- --tenant clinic-haddad --path knowledge/clinic-haddad
//   dotnet run --project src/Knowledge.Ingestion -- --tenant clinic-haddad --ask "do you accept insurance?"
// Defaults in LlmOptions/KnowledgeOptions target the local docker stack; override with --Llm:Endpoint=... etc.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAssistantKnowledge(builder.Configuration);
using var host = builder.Build();

var cfg = host.Services.GetRequiredService<IConfiguration>();
var tenant = cfg["tenant"] ?? throw new ArgumentException("--tenant is required");
var index = host.Services.GetRequiredService<KnowledgeIndex>();
await index.EnsureCollectionAsync();

if (cfg["path"] is { } path)
{
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

if (cfg["ask"] is { } question)
{
    Console.WriteLine($"\nQ: {question}");
    foreach (var hit in await index.SearchAsync(tenant, question))
        Console.WriteLine($"  {hit.Score:0.000}  {hit.Source,-26} {hit.Heading}");
}
