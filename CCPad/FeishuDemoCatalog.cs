using System;
using System.IO;
using System.Text.Json;

namespace CCPad;

internal sealed record FeishuDemoBot(string Name, string AppId, string ChatId, string CredentialsFile);

internal sealed record FeishuDemoCatalog(string PythonExe, string ScriptFile, FeishuDemoBot[] Bots)
{
    internal static FeishuDemoCatalog Read(string path)
    {
        var catalog = JsonSerializer.Deserialize<FeishuDemoCatalog>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Demo Bot configuration unavailable");
        if (!Path.IsPathFullyQualified(catalog.PythonExe) || !File.Exists(catalog.PythonExe) ||
            !Path.IsPathFullyQualified(catalog.ScriptFile) || !File.Exists(catalog.ScriptFile) ||
            catalog.Bots == null || catalog.Bots.Length == 0)
            throw new InvalidOperationException("Demo runtime unavailable");
        return catalog;
    }

    internal FeishuStartRequest Request(FeishuDemoBot bot, string paneId, string stateRoot)
    {
        if (Array.IndexOf(Bots, bot) < 0 || string.IsNullOrWhiteSpace(bot.Name) ||
            string.IsNullOrWhiteSpace(bot.AppId) || string.IsNullOrWhiteSpace(bot.ChatId) ||
            !Path.IsPathFullyQualified(bot.CredentialsFile) || !File.Exists(bot.CredentialsFile))
            throw new InvalidOperationException("Selected Demo Bot is not configured");
        // Every connection owns its queue. A later connection never replays a previous handoff.
        return new(paneId, bot.AppId, bot.ChatId, PythonExe, ScriptFile, bot.CredentialsFile,
            Path.Combine(stateRoot, Guid.NewGuid().ToString("N")));
    }
}
