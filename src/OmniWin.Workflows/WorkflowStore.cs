using System.Text.Json;
using System.Text.RegularExpressions;
using OmniWin.Core;

namespace OmniWin.Workflows;

/// <summary>
/// One saved step. Declarative data: a tool name, literal arguments, a selector, and a placeholder name for any text.
/// <see cref="TargetEmptyBefore"/> (contract 2.6, optional): for a type step, whether the field was empty when it was learned.
/// </summary>
public sealed record WorkflowStep(
    string Tool,
    string Engine,
    string Action,
    ElementDescriptor? Target,
    IReadOnlyDictionary<string, string> Args,
    string? ValueParam,
    bool? TargetEmptyBefore = null);

/// <summary>A saved workflow file under the data dir's <c>workflows\</c> folder.</summary>
public sealed record Workflow(
    int Version,
    string Name,
    string Description,
    DateTimeOffset SavedAt,
    IReadOnlyDictionary<string, string> Params,     // parameter name -> where it is used ("step 2 value")
    IReadOnlyList<WorkflowStep> Steps);

public sealed record WorkflowSummary(string Name, string Description, int StepCount, IReadOnlyList<string> ParamNames, DateTimeOffset SavedAt);

public enum StoreStatus { Ok, Exists, InvalidName, NotFound, Invalid, IoError }

public sealed record StoreResult(StoreStatus Status, string? Detail = null);

/// <summary>
/// Reads and writes workflow files. Never deletes or replaces a file: saving a name that exists is refused. Workflow
/// files are plain JSON; loading validates every step against the known tool list, so a hand-edited file cannot smuggle in
/// anything that is not a normal tool call.
/// </summary>
public sealed partial class WorkflowStore
{
    public const int CurrentVersion = 1;
    public const int MaxSteps = 200;

    private readonly string _dir;

    public WorkflowStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _dir = directory;
    }

    public string Directory => _dir;

    /// <summary>Workflow and parameter names: letters, digits, underscore and hyphen, 1 to 64 characters. This also keeps a name from ever being a path.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>Parameter names: lower case letter first, then lower case letters, digits and underscore, up to 32 characters.</summary>
    public static bool IsValidParamName(string? name) => name is not null && ParamPattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex ParamPattern();

    private string PathOf(string name) => Path.Combine(_dir, name + ".json");

    public int Count()
    {
        try
        {
            return System.IO.Directory.Exists(_dir) ? System.IO.Directory.EnumerateFiles(_dir, "*.json").Count() : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public StoreResult Save(Workflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (!IsValidName(workflow.Name)) return new StoreResult(StoreStatus.InvalidName, "A workflow name is 1 to 64 letters, digits, '_' or '-', starting with a letter or digit.");
        var path = PathOf(workflow.Name);
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            // CreateNew: refuses (never replaces) an existing file, and the check is atomic.
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, workflow, StepTools.JsonOptions);
            return new StoreResult(StoreStatus.Ok);
        }
        catch (IOException) when (File.Exists(path))
        {
            return new StoreResult(StoreStatus.Exists, $"A workflow named \"{workflow.Name}\" already exists. Pick another name.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StoreResult(StoreStatus.IoError, "The workflow could not be written: " + ex.Message);
        }
    }

    public (StoreResult Result, Workflow? Workflow) Load(string name)
    {
        if (!IsValidName(name)) return (new StoreResult(StoreStatus.InvalidName, "Not a valid workflow name."), null);
        var path = PathOf(name);
        if (!File.Exists(path)) return (new StoreResult(StoreStatus.NotFound, $"No workflow named \"{name}\"."), null);
        try
        {
            using var stream = File.OpenRead(path);
            var workflow = JsonSerializer.Deserialize<Workflow>(stream, StepTools.JsonOptions);
            var problem = Validate(workflow);
            return problem is null
                ? (new StoreResult(StoreStatus.Ok), workflow)
                : (new StoreResult(StoreStatus.Invalid, problem), null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (new StoreResult(StoreStatus.Invalid, "The workflow file could not be read: " + ex.Message), null);
        }
    }

    public IReadOnlyList<WorkflowSummary> List()
    {
        var list = new List<WorkflowSummary>();
        if (!System.IO.Directory.Exists(_dir)) return list;
        IEnumerable<string> files;
        try
        {
            files = System.IO.Directory.EnumerateFiles(_dir, "*.json").Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return list;
        }

        foreach (var file in files)
        {
            var (result, workflow) = Load(Path.GetFileNameWithoutExtension(file));
            if (result.Status == StoreStatus.Ok && workflow is not null)
            {
                list.Add(new WorkflowSummary(workflow.Name, workflow.Description, workflow.Steps.Count, [.. workflow.Params.Keys.Order(StringComparer.Ordinal)], workflow.SavedAt));
            }
        }

        return list;
    }

    /// <summary>Returns why a loaded workflow is not acceptable, or null.</summary>
    public static string? Validate(Workflow? workflow)
    {
        if (workflow is null) return "The file is empty.";
        if (workflow.Version != CurrentVersion) return $"Unsupported workflow version {workflow.Version}.";
        if (!IsValidName(workflow.Name)) return "The workflow has an invalid name.";
        if (workflow.Steps.Count is 0 or > MaxSteps) return $"A workflow has 1 to {MaxSteps} steps.";
        for (var i = 0; i < workflow.Steps.Count; i++)
        {
            var step = workflow.Steps[i];
            if (!StepTools.IsKnown(step.Tool)) return $"Step {i + 1} uses \"{step.Tool}\", which is not a tool a workflow may call.";
            if (step.Engine != StepTools.EngineOf(step.Tool)) return $"Step {i + 1} names the wrong engine for {step.Tool}.";
            if (StepTools.IsRefBased(step.Tool) && step.Target is null) return $"Step {i + 1} has no selector.";
            if (step.ValueParam is not null && !workflow.Params.ContainsKey(step.ValueParam)) return $"Step {i + 1} uses parameter \"{step.ValueParam}\" that the workflow does not declare.";
            if (step.ValueParam is not null && !IsValidParamName(step.ValueParam)) return $"Step {i + 1} has an invalid parameter name.";
        }

        return null;
    }
}
