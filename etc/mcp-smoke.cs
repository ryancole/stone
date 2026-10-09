#:package ModelContextProtocol@2.2.0
// MCP end-to-end check: a real stdio client drives `behavediff mcp` against a throwaway repo.
// Usage (via etc/mcp-smoke.ps1): dotnet run etc/mcp-smoke.cs -- <behavediff.dll> <repo>
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var cli = args[0];
var repo = args[1];
var failures = 0;
void Check(string name, bool ok, string detail)
{
    if (!ok) failures++;
    Console.WriteLine($"  {name}: {(ok ? "PASS" : "FAIL")}  {detail}");
}

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
var ct = timeout.Token;
await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "behavediff",
    Command = "dotnet",
    Arguments = [cli, "mcp", "--repo", repo],
    WorkingDirectory = repo,
    StandardErrorLines = line => Console.Error.WriteLine($"    [server] {line}"),
}), cancellationToken: ct);

var tools = await client.ListToolsAsync(cancellationToken: ct);
var names = tools.Select(t => t.Name).OrderBy(n => n).ToArray();
Check("list_tools", names.SequenceEqual(["accept_difference", "check_behavior_changes", "explain_difference"]), string.Join(", ", names));

async Task<(bool IsError, string Text)> Call(string tool, Dictionary<string, object?> arguments)
{
    var progressCount = 0;
    var result = await client.CallToolAsync(tool, arguments, new Progress<ProgressNotificationValue>(_ => progressCount++), cancellationToken: ct);
    var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    if (tool == "check_behavior_changes") Console.WriteLine($"    ({progressCount} progress notifications)");
    return (result.IsError == true, text);
}

// 1. Check: the working tree changes a DB default, so differences are unexpected.
var (err, text) = await Call("check_behavior_changes", new() { ["intent"] = "Default new widgets to Low priority" });
var report = JsonNode.Parse(text)!;
var unexpected = report["differences"]!.AsArray().Where(d => (string?)d!["classification"] == "Unexpected").ToList();
Check("check (no expect)", !err && (int)report["exitCode"]! == 1 && unexpected.Count > 0,
    $"exitCode={report["exitCode"]} unexpected={unexpected.Count} intent=\"{report["intent"]!["text"]}\"");

// 2. Explain the first difference: both raw observations are there.
var id = (string)unexpected[0]!["id"]!;
(err, text) = await Call("explain_difference", new() { ["id"] = id });
var detail = JsonNode.Parse(text)!;
Check("explain", !err && detail["base"]?["data"]?["values"]?["Priority"] is not null && detail["current"]?["data"]?["values"]?["Priority"] is not null,
    $"{id}: base Priority={detail["base"]?["data"]?["values"]?["Priority"]} current Priority={detail["current"]?["data"]?["values"]?["Priority"]} test={detail["difference"]?["test"]}");

// 3. Accept it with a reason; an unknown id and an empty reason are errors.
(err, text) = await Call("accept_difference", new() { ["id"] = id, ["reason"] = "Low is the new product default for widgets." });
Check("accept", !err && text.Contains(id), text.ReplaceLineEndings(" "));
(err, text) = await Call("accept_difference", new() { ["id"] = "d-00000000", ["reason"] = "typo" });
Check("accept unknown id is an error", err, text);
(err, text) = await Call("accept_difference", new() { ["id"] = id, ["reason"] = " " });
Check("accept without reason is an error", err, text);

// 4. Check again with an expect rule: the accepted one is Accepted, the rest Intended, nothing Unexpected.
(err, text) = await Call("check_behavior_changes", new() { ["intent"] = "Default new widgets to Low priority", ["expect"] = new[] { "entity:Widget" } });
report = JsonNode.Parse(text)!;
var byId = report["differences"]!.AsArray().ToDictionary(d => (string)d!["id"]!, d => (string)d!["classification"]!);
var s = report["summary"]!;
Check("re-check", !err && byId.GetValueOrDefault(id) == "Accepted" && (int)s["unexpected"]! == 0 && (int)s["intended"]! == unexpected.Count - 1,
    $"{id}={byId.GetValueOrDefault(id)} unexpected={s["unexpected"]} intended={s["intended"]} accepted={s["accepted"]}");

Console.WriteLine(failures == 0 ? "MCP smoke: all passed" : $"MCP smoke: {failures} failed");
return failures == 0 ? 0 : 1;
