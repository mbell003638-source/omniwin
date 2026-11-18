using System.Text.Json;
using System.Text.Json.Serialization;
using OmniWin.Core;

namespace OmniWin.Safety;

public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Loads config.json, creating it with defaults on first run. Strict on purpose: an unknown key (for example a
/// misspelled blocklist entry) or an invalid value stops the server instead of being silently ignored.
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions ReadOptions = new(CoreJson.Indented)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static OmniWinConfig LoadOrCreate(OmniWinPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.DataDir);

        if (!File.Exists(paths.ConfigFile))
        {
            var defaults = new OmniWinConfig();
            TryWriteNew(paths.ConfigFile, defaults);
            if (!File.Exists(paths.ConfigFile))
            {
                throw new ConfigException($"could not create {paths.ConfigFile}");
            }
        }

        OmniWinConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<OmniWinConfig>(File.ReadAllText(paths.ConfigFile), ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{paths.ConfigFile} is not valid: {ex.Message}", ex);
        }

        if (config is null)
        {
            throw new ConfigException($"{paths.ConfigFile} is empty");
        }

        var errors = config.Validate();
        if (errors.Count > 0)
        {
            throw new ConfigException($"{paths.ConfigFile}: {string.Join("; ", errors)}");
        }

        return config;
    }

    /// <summary>
    /// Contract 2.6: checks a config file the way the server would at start-up, without creating or changing anything. Returns the
    /// problems in plain words (empty when the file is usable). The settings window uses it so the rules live in one place.
    /// </summary>
    public static IReadOnlyList<string> Check(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        if (!File.Exists(file)) return [$"{file} does not exist"];
        try
        {
            var config = JsonSerializer.Deserialize<OmniWinConfig>(File.ReadAllText(file), ReadOptions);
            return config is null ? ["the file is empty"] : config.Validate();
        }
        catch (JsonException ex)
        {
            return ["not valid JSON for OmniWin: " + ex.Message];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ["the file could not be read: " + ex.Message];
        }
    }

    public static string Serialize(OmniWinConfig config) => JsonSerializer.Serialize(config, CoreJson.Indented);

    private static void TryWriteNew(string file, OmniWinConfig config)
    {
        try
        {
            // CreateNew: never overwrite a config another process wrote in the meantime.
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(Serialize(config));
        }
        catch (IOException) when (File.Exists(file))
        {
            // Someone else created it first; read theirs.
        }
    }
}
