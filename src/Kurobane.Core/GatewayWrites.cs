using System.Text.Json.Nodes;

namespace Kurobane.Core;

/// <summary>
/// Request bodies for the ZeroClaw routes this client is allowed to write.
/// Cron matches gateway 99bd353: POST /api/cron takes a string cron expression, a required agent,
/// and either an agent prompt or a shell command. Personality writes are allowlisted filenames only.
/// </summary>
public static class GatewayWrites
{
    public static readonly string[] PersonalityFiles =
    [
        "SOUL.md", "IDENTITY.md", "USER.md", "AGENTS.md", "TOOLS.md", "HEARTBEAT.md", "BOOTSTRAP.md", "MEMORY.md"
    ];

    public static bool PersonalityFilenameAllowed(string? filename)
        => filename is not null && PersonalityFiles.Contains(filename, StringComparer.Ordinal);

    public static JsonObject CronAdd(string agent, string schedule, string? name, string? prompt, string? command)
    {
        var body = new JsonObject
        {
            ["agent"] = agent,
            ["schedule"] = schedule,
        };
        if (!string.IsNullOrWhiteSpace(name)) body["name"] = name.Trim();
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            body["job_type"] = "agent";
            body["prompt"] = prompt;
        }
        else
        {
            body["job_type"] = "shell";
            body["command"] = command ?? "";
        }
        return body;
    }

    public static JsonObject CronEnabled(bool enabled) => new() { ["enabled"] = enabled };

    public static JsonObject PersonalityPut(string content, long? expectedMtimeMs)
    {
        var body = new JsonObject { ["content"] = content };
        if (expectedMtimeMs is long mtime) body["expected_mtime_ms"] = mtime;
        return body;
    }
}
