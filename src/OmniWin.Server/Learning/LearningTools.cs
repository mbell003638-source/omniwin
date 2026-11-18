using System.ComponentModel;
using ModelContextProtocol.Server;

namespace OmniWin.Server.Learning;

/// <summary>task_done and workflow_manage (contract 2.6). Neither sends input.</summary>
[McpServerToolType]
internal sealed class LearningTools(LearningService learning)
{
    [McpServerTool(Name = "task_done", Title = "Finish a task so it can be learned", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Call when a task you did for the user is finished. success=true saves the acts that worked since omniwin_do answered " +
        "needs_brain as a recipe, so the same request can be replayed without thinking between steps. If typed text changes from " +
        "request to request, pass template (the request with each changing part as {slot}, for example \"open {file} in word\") and " +
        "params mapping each text placeholder to its slot ({\"p1\":\"file\"}; placeholders are named in the answer if you leave it out). " +
        "goal is only needed when you did not call omniwin_do first. success=false learns nothing. Sends no input; nothing is ever " +
        "replaced or deleted.")]
    public string TaskDone(bool success, string? goal = null, string? template = null, Dictionary<string, string>? @params = null) =>
        learning.TaskDone(success, goal, template, @params);

    [McpServerTool(Name = "workflow_manage", Title = "Disable, enable or rate a learned recipe", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("For a learned recipe (see workflow_list): action is disable (never offered or run by itself again), enable, right " +
        "(the user says the last run was the right thing) or wrong_pick (it ran for the wrong request: it loses its trust). " +
        "Only a note is added; nothing is deleted. Sends no input.")]
    public string WorkflowManage(string name, string action) => learning.Manage(name, action);
}
