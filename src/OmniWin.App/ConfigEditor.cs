using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmniWin.App;

/// <summary>The handful of OmniWin settings the window lets the owner change. Everything else in config.json is left as it is.</summary>
internal sealed record ConfigView(
    bool LearningEnabled, bool LearningAutoRun, int PromoteAfter,
    bool RouterEnabled,
    string DecisionProvider, string DecisionMode, double DecisionThreshold, bool DecisionAllowRemote,
    bool EnginesEnabled,
    bool PowerToolsEnabled, bool AllowDelete);   // the last two are shown, never written

/// <summary>
/// Edits OmniWin's config.json for the settings window. It never decides what is valid: the candidate file is checked by
/// <c>OmniWin.Server.exe --validate-config</c> (the server's own rules) and only a file that passes replaces config.json, by one
/// rename. The safety switches (power tools, deleting) are read for display and are never written from here.
/// </summary>
internal sealed class ConfigEditor(string configFile, string serverExe)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private JsonObject Read()
    {
        if (!File.Exists(configFile)) return [];
        return JsonNode.Parse(File.ReadAllText(configFile)) as JsonObject ?? [];
    }

    private static T Get<T>(JsonObject root, string section, string key, T fallback)
    {
        try
        {
            return root[section]?[key] is JsonValue value && value.TryGetValue<T>(out var got) ? got : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    public ConfigView Load()
    {
        var root = Read();
        return new ConfigView(
            Get(root, "learning", "enabled", true), Get(root, "learning", "auto_run", true), Get(root, "learning", "promote_after", 3),
            Get(root, "router", "enabled", true),
            Get(root, "decision", "provider", "none"), Get(root, "decision", "mode", "off"), Get(root, "decision", "threshold", 0.95),
            Get(root, "decision", "allow_remote", false),
            Get(root, "engines", "enabled", true),
            Get(root, "power_tools", "enabled", false), Get(root, "power_tools", "allow_delete", false));
    }

    private static void Set(JsonObject root, string section, string key, JsonNode value)
    {
        if (root[section] is not JsonObject obj)
        {
            obj = [];
            root[section] = obj;
        }

        obj[key] = value;
    }

    /// <summary>Saves the editable settings. Returns the problems in plain words; empty means config.json was replaced.</summary>
    public IReadOnlyList<string> Save(ConfigView view)
    {
        JsonObject root;
        try
        {
            root = Read();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ["config.json could not be read, so nothing was changed: " + ex.Message];
        }

        Set(root, "learning", "enabled", view.LearningEnabled);
        Set(root, "learning", "auto_run", view.LearningAutoRun);
        Set(root, "learning", "promote_after", view.PromoteAfter);
        Set(root, "router", "enabled", view.RouterEnabled);
        Set(root, "decision", "provider", view.DecisionProvider.Trim());
        Set(root, "decision", "mode", view.DecisionMode.Trim());
        Set(root, "decision", "threshold", view.DecisionThreshold);
        Set(root, "decision", "allow_remote", view.DecisionAllowRemote);
        Set(root, "engines", "enabled", view.EnginesEnabled);
        // power_tools.* is deliberately not written: turning those on stays a hand edit of the file.

        var candidate = configFile + ".new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
            File.WriteAllText(candidate, root.ToJsonString(Indented));
            var problems = Check(candidate);
            if (problems.Count > 0)
            {
                File.Delete(candidate);   // this app's own scratch file
                return problems;
            }

            File.Move(candidate, configFile, overwrite: true);
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ["The settings could not be saved: " + ex.Message];
        }
    }

    /// <summary>Asks the server whether a config file is usable.</summary>
    public IReadOnlyList<string> Check(string file)
    {
        if (!File.Exists(serverExe)) return ["OmniWin.Server.exe was not found at " + serverExe + ", so the settings could not be checked."];
        var info = new ProcessStartInfo(serverExe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--validate-config");
        info.ArgumentList.Add(file);
        using var process = Process.Start(info);
        if (process is null) return ["OmniWin.Server.exe could not be started to check the settings."];
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(15000))
        {
            process.Kill();
            return ["Checking the settings took too long."];
        }

        try
        {
            var reply = JsonNode.Parse(output);
            if (reply?["ok"]?.GetValue<bool>() == true) return [];
            return [.. (reply?["errors"]?.AsArray() ?? []).Select(e => e?.GetValue<string>() ?? "unknown problem")];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ["The server's answer could not be read; nothing was changed."];
        }
    }
}
