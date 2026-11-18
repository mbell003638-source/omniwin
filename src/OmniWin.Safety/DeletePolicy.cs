using System.Text;
using System.Text.RegularExpressions;
using OmniWin.Core;

namespace OmniWin.Safety;

/// <summary>
/// The owner's rule (2026-11-06): OmniWin may do anything except delete. No confirmation prompts, no app is off
/// limits (terminals included). This decides which actions count as deleting: delete-like buttons and menu items,
/// delete keys in Explorer, and typed delete commands in places that run commands (terminals, the Run box,
/// Explorer's address bar). It is a best-effort filter on names, keys and text, not a sandbox: an unnamed trash icon,
/// or a script that deletes as a side effect, can't be recognised.
/// </summary>
public static partial class DeletePolicy
{
    /// <summary>
    /// Only the secure desktop and sign-in prompts. Windows already refuses injected input there; listing them gives
    /// a clear reason instead of a silent failure. Users can add more with "process_blocklist_extra".
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultBlockedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "consent.exe", "LogonUI.exe", "CredentialUIBroker.exe",
    };

    /// <summary>
    /// OmniWin's own programs (contract 2.6): the server and the OmniWin app with its command box and settings window. An AI client
    /// must not be able to click or type in them through OmniWin, or it could change the owner's settings or feed itself requests.
    /// </summary>
    public static readonly IReadOnlySet<string> OwnProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "OmniWin.Server.exe", "OmniWin.App.exe", "omniwin-app.exe",
    };

    /// <summary>Processes where typed text is run as a command, so delete commands are checked.</summary>
    public static readonly IReadOnlySet<string> CommandProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "powershell_ise.exe", "WindowsTerminal.exe", "wt.exe", "conhost.exe",
        "OpenConsole.exe", "wsl.exe", "bash.exe", "mintty.exe", "explorer.exe",
    };

    // Whole words or phrases in an element's name (case-insensitive).
    [GeneratedRegex(
        @"\b(delete|remove|erase|discard|uninstall|wipe|shred|" +
        @"move to (the )?(trash|bin|recycle bin)|empty (the )?(trash|bin|recycle bin)|permanently delete|" +
        @"clear (all )?(browsing data|history|cache|cookies)|delete history|clear all|reset this pc|factory reset)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeleteName();

    // Fragments of AutomationIds / test ids ("DeleteButton", "btn-remove").
    private static readonly string[] DeleteIdFragments = ["delete", "remove", "erase", "discard", "trash", "uninstall"];

    [GeneratedRegex(@"^\s*(?:(?:PS\s+)?[a-z]:[^>\r\n]*>\s*|[\w.-]+@[\w.-]+(?::[^\r\n$#]*)?[$#]\s*|[$#]\s+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShellPrompt();

    [GeneratedRegex(@"(?:^|[. _-])(?:xterm(?:$|[ _-])|(?:terminalcontrol|terminalinput|terminaltextarea|terminalemulator|termcontrol|consolewindowclass|consolepane|consolecontrol|pseudoconsole|terminal)(?:$|[ _-]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TerminalControl();

    private static readonly HashSet<string> EditorProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code.exe", "Code - Insiders.exe", "Cursor.exe", "Windsurf.exe", "Antigravity.exe", "devenv.exe",
        "idea64.exe", "rider64.exe", "pycharm64.exe", "webstorm64.exe", "clion64.exe", "powershell_ise.exe",
    };

    /// <summary>Returns why the action is forbidden, or null when it is allowed.</summary>
    public static string? WhyForbidden(ActionDescriptor a, IReadOnlySet<string> blockedProcesses, IReadOnlyList<string> extraWords)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(blockedProcesses);
        ArgumentNullException.ThrowIfNull(extraWords);

        var process = a.Target?.Process ?? a.Process;
        if (process is not null && blockedProcesses.Contains(process))
        {
            return $"{process} can't be automated (secure desktop / sign-in, or listed in process_blocklist_extra).";
        }

        if (ActivatesTarget(a) && a.Target is { } t)
        {
            if (IsDeleteName(t.Name, extraWords))
            {
                return $"\"{t.Name}\" looks like a delete action. OmniWin is set to never delete.";
            }

            if (t.AutomationId is { Length: > 0 } id && ContainsAny(id, DeleteIdFragments))
            {
                return $"element id \"{id}\" looks like a delete action. OmniWin is set to never delete.";
            }
        }

        if (a.Action == "drag" && a.DropTarget is { } drop)
        {
            // Dropping an item on the Recycle Bin (or on a delete button) deletes it.
            if (IsDeleteName(drop.Name, extraWords) || DropTargetIsBin(drop.Name) ||
                drop.AutomationId is { Length: > 0 } dropId && ContainsAny(dropId, DeleteIdFragments))
            {
                return $"dragging onto \"{drop.Name}\" looks like deleting. OmniWin is set to never delete.";
            }
        }

        if (a.Keys is { Length: > 0 } keys && WhyKeysForbidden(keys, process, a.Target) is { } keyReason)
        {
            return keyReason;
        }

        if (a.ContainsDeleteCommand || a.ValuePreview is { Length: > 0 } text &&
            IsCommandTarget(a.Target, process, a.WindowTitle) && IsDeleteCommand(text))
        {
            return $"typed text contains a delete command for {process}. OmniWin is set to never delete.";
        }

        return null;
    }

    public static bool IsDeleteName(string? name, IReadOnlyList<string> extraWords)
    {
        ArgumentNullException.ThrowIfNull(extraWords);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (DeleteName().IsMatch(name))
        {
            return true;
        }

        foreach (var word in extraWords)
        {
            if (word.Length > 0 && Regex.IsMatch(name, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Recognizes direct commands and common command wrappers. This is a lexical heuristic.</summary>
    public static bool IsDeleteCommand(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var scripts = new Stack<string>();
        scripts.Push(text);
        while (scripts.TryPop(out var script))
        {
            foreach (var command in Commands(script))
            {
                if (IsDestructiveCommand(command, scripts)) return true;
            }
        }

        return false;
    }

    /// <summary>Checks one currently visible command line, allowing common Windows and POSIX prompt prefixes.</summary>
    public static bool IsDeleteCommandLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var prompt = ShellPrompt().Match(line);
        return IsDeleteCommand(prompt.Success ? line[prompt.Length..] : line);
    }

    public static bool IsCommandSubmission(string keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var chord in keys.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (CanonicalChord(chord) is "enter" or "ctrl+enter") return true;
        }

        return false;
    }

    public static bool IsCommandTarget(ElementDescriptor? target, string? process, string? windowTitle)
    {
        process ??= target?.Process;
        if (target is not null && HasTerminalContext(target, process))
        {
            return true;
        }

        if (process is null || !CommandProcesses.Contains(process) ||
            process.Equals("powershell_ise.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!process.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(windowTitle, "Run", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (target is null)
        {
            return false;
        }

        if (target.Name.StartsWith("Address", StringComparison.OrdinalIgnoreCase) ||
            target.AutomationId?.Contains("address", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        foreach (var segment in target.Path)
        {
            if (segment.ClassName?.Contains("Address", StringComparison.OrdinalIgnoreCase) == true ||
                segment.AutomationId?.Contains("address", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <param name="keys">Normalized chord sequence, e.g. "ctrl+a delete".</param>
    public static string? WhyKeysForbidden(string keys, string? process) => WhyKeysForbidden(keys, process, null);

    public static string? WhyKeysForbidden(string keys, string? process, ElementDescriptor? target)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var isExplorer = string.Equals(process, "explorer.exe", StringComparison.OrdinalIgnoreCase);
        var fileView = IsFileListTarget(target) || isExplorer && (target is null || !IsTextTarget(target));
        foreach (var chord in keys.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var c = CanonicalChord(chord);
            if (c is "shift+delete" or "shift+del")
            {
                return "shift+delete permanently deletes. OmniWin is set to never delete.";
            }

            if (c is "ctrl+shift+delete" or "ctrl+shift+del")
            {
                return "ctrl+shift+delete opens \"clear browsing data\". OmniWin is set to never delete.";
            }

            if (fileView && (c is "delete" or "ctrl+delete" or "ctrl+d") || isExplorer && c == "ctrl+d")
            {
                return $"{chord} can delete the selected files. OmniWin is set to never delete.";
            }
        }

        return null;
    }

    /// <summary>Requires a file-view role and shell control evidence, including views hosted by other apps.</summary>
    public static bool IsFileListTarget(ElementDescriptor? target)
    {
        if (target is null || target.Role is not ("list" or "listitem" or "dataitem" or "tree" or "treeitem" or "datagrid" or "table" or "pane"))
        {
            return false;
        }

        var directUi = false;
        var dialogView = false;
        var itemsView = IsItemsViewId(target.AutomationId);
        var shellView = false;
        foreach (var segment in target.Path)
        {
            if (segment.Role is "edit" or "textbox" or "combobox" or "spinner") return false;
            if (segment.ClassName is { } className)
            {
                if (className.Equals("SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase) ||
                    className.Equals("NamespaceTreeControl", StringComparison.OrdinalIgnoreCase) ||
                    className.Equals("UIItemsView", StringComparison.OrdinalIgnoreCase))
                {
                    shellView = true;
                }

                directUi |= className.Equals("DirectUIHWND", StringComparison.OrdinalIgnoreCase);
                dialogView |= className.Equals("DUIViewWndClassName", StringComparison.OrdinalIgnoreCase);
            }

            itemsView |= IsItemsViewId(segment.AutomationId);
        }

        return shellView || directUi && (dialogView || itemsView);
    }

    private static bool IsItemsViewId(string? id) => id is not null &&
        (id.Equals("ItemsView", StringComparison.OrdinalIgnoreCase) || id.Equals("FolderView", StringComparison.OrdinalIgnoreCase));

    private static bool IsTextTarget(ElementDescriptor target) =>
        target.Role is "edit" or "document" or "textbox" or "combobox" or "spinner";

    private static bool HasTerminalContext(ElementDescriptor target, string? process)
    {
        if (!IsTextTarget(target) && target.Role is not ("pane" or "custom" or "terminal")) return false;
        if (TerminalIdentity(target.AutomationId)) return true;
        foreach (var segment in target.Path)
        {
            if (TerminalIdentity(segment.AutomationId) || TerminalIdentity(segment.ClassName))
            {
                return true;
            }

            if (process is not null && EditorProcesses.Contains(process) &&
                (segment.Role is "pane" or "group" or "custom") &&
                (string.Equals(segment.Name, "Terminal", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(segment.Name, "Integrated Terminal", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return target.Role == "terminal";
    }

    private static bool TerminalIdentity(string? value) => value is not null && TerminalControl().IsMatch(value);

    private static bool IsDestructiveCommand(IReadOnlyList<CommandToken> command, Stack<string> scripts)
    {
        for (var start = 0; start < command.Count;)
        {
            var name = ExecutableName(command[start].Value);
            if (name is "do" or "then" or "else" or "call")
            {
                start++;
                continue;
            }

            if (name == "if")
            {
                start = AfterIfCondition(command, start + 1);
                continue;
            }

            if (name == "start")
            {
                start = AfterStart(command, start + 1);
                continue;
            }

            if (name is "sudo" or "doas" or "env" or "wsl" or "xargs" or "nohup" or "exec" or "command")
            {
                start = AfterWrapper(command, start, name);
                continue;
            }

            if (EnvironmentAssignment(command[start].Value))
            {
                start++;
                continue;
            }

            if (name is "del" or "erase" or "rd" or "rmdir" or "rm" or "ri" or "unlink" or "shred" or "sdelete" or
                "format" or "diskpart" or "remove-item" or "clear-recyclebin" or "clear-content" or
                "format-volume" or "clear-disk" or "remove-partition")
            {
                return true;
            }

            if (name is "cmd" or "pwsh" or "powershell" or "sh" or "bash" or "dash" or "zsh" or "ksh" or "fish")
            {
                for (var i = start + 1; i < command.Count; i++)
                {
                    var option = command[i].Value.ToLowerInvariant();
                    var scriptOption = name == "cmd" ? option is "/c" or "/k" :
                        name is "pwsh" or "powershell" ? option is "-c" or "-command" or "-commandwithargs" :
                        option == "--command" || option.StartsWith('-') && !option.StartsWith("--", StringComparison.Ordinal) && option.Contains('c');
                    if (scriptOption && i + 1 < command.Count)
                    {
                        scripts.Push(CommandBody(command, i + 1));
                        break;
                    }
                }

                return false;
            }

            if (name == "git")
            {
                var subcommand = AfterOptions(command, start + 1, ["-C", "-c", "--git-dir", "--work-tree", "--namespace"]);
                return subcommand < command.Count && (command[subcommand].Value.ToLowerInvariant() is "clean" or "rm");
            }

            if (name == "reg" || name == "vssadmin")
            {
                return start + 1 < command.Count && command[start + 1].Value.Equals("delete", StringComparison.OrdinalIgnoreCase);
            }

            if (name == "wmic")
            {
                return command.Skip(start + 1).Any(token => token.Value.Equals("delete", StringComparison.OrdinalIgnoreCase));
            }

            if (name == "cipher")
            {
                return command.Skip(start + 1).Any(token => token.Value.Equals("/w", StringComparison.OrdinalIgnoreCase) ||
                    token.Value.StartsWith("/w:", StringComparison.OrdinalIgnoreCase));
            }

            if (name == "robocopy")
            {
                return command.Skip(start + 1).Any(token => token.Value.Equals("/mir", StringComparison.OrdinalIgnoreCase) ||
                    token.Value.Equals("/purge", StringComparison.OrdinalIgnoreCase));
            }

            if (name == "find")
            {
                for (var i = start + 1; i < command.Count; i++)
                {
                    var option = command[i].Value.ToLowerInvariant();
                    if (option == "-delete") return true;
                    if (option is "-exec" or "-execdir" or "-ok" or "-okdir")
                    {
                        var end = i + 1;
                        while (end < command.Count && command[end].Value is not (";" or "+")) end++;
                        if (end > i + 1) scripts.Push(string.Join(' ', command.Skip(i + 1).Take(end - i - 1).Select(token => token.Raw)));
                        i = end;
                    }
                    else if (option is "-name" or "-iname" or "-path" or "-ipath" or "-wholename" or "-iwholename" or
                        "-regex" or "-iregex" or "-lname" or "-ilname" or "-printf" or "-fprint" or "-fprint0" or "-fls")
                    {
                        i++;
                    }
                    else if (option == "-fprintf")
                    {
                        i += 2;
                    }
                }
            }

            return false;
        }

        return false;
    }

    private static int AfterIfCondition(IReadOnlyList<CommandToken> command, int start)
    {
        var index = start;
        if (index < command.Count && command[index].Value.Equals("/i", StringComparison.OrdinalIgnoreCase)) index++;
        if (index < command.Count && command[index].Value.Equals("not", StringComparison.OrdinalIgnoreCase)) index++;
        if (index >= command.Count) return index;
        var condition = command[index].Value.ToLowerInvariant();
        if (condition is "exist" or "defined" or "errorlevel" or "cmdextversion") return Math.Min(index + 2, command.Count);
        if (condition.Contains("==", StringComparison.Ordinal)) return index + 1;
        if (index + 1 < command.Count &&
            command[index + 1].Value.ToLowerInvariant() is "==" or "equ" or "neq" or "lss" or "leq" or "gtr" or "geq")
        {
            return Math.Min(index + 3, command.Count);
        }

        // POSIX conditions may themselves invoke a command. Bracket/test conditions remain ordinary tokens.
        return index;
    }

    private static int AfterStart(IReadOnlyList<CommandToken> command, int start)
    {
        var titleSeen = false;
        for (var index = start; index < command.Count; index++)
        {
            var token = command[index];
            var option = token.Value.ToLowerInvariant();
            if (option is "/d" or "/node" or "/affinity" or "/machine")
            {
                index++;
            }
            else if (option is "/b" or "/i" or "/min" or "/max" or "/wait" or "/separate" or "/shared" or
                "/low" or "/normal" or "/high" or "/realtime" or "/abovenormal" or "/belownormal")
            {
                continue;
            }
            else if (!titleSeen && token.Raw.StartsWith('"'))
            {
                titleSeen = true;
            }
            else
            {
                return index;
            }
        }

        return command.Count;
    }

    private static int AfterWrapper(IReadOnlyList<CommandToken> command, int start, string wrapper)
    {
        var arguments = wrapper switch
        {
            "sudo" => new[] { "-u", "--user", "-g", "--group", "-h", "--host", "-p", "--prompt", "-C", "-D", "-R", "-T" },
            "doas" => ["-u", "-C"],
            "env" => ["-u", "--unset", "-C", "--chdir"],
            "wsl" => ["-d", "--distribution", "-u", "--user", "--cd", "--shell-type"],
            "xargs" => ["-n", "--max-args", "-P", "--max-procs", "-I", "--replace", "-L", "--max-lines", "-s", "--max-chars", "-E", "-d", "--delimiter"],
            _ => [],
        };
        var index = AfterOptions(command, start + 1, arguments);
        while (index < command.Count && EnvironmentAssignment(command[index].Value)) index++;
        return index;
    }

    private static int AfterOptions(IReadOnlyList<CommandToken> command, int start, IReadOnlyList<string> arguments)
    {
        var index = start;
        while (index < command.Count)
        {
            var token = command[index].Value;
            if (token == "--") return index + 1;
            if (!token.StartsWith('-')) return index;
            index += arguments.Contains(token, StringComparer.Ordinal) ? 2 : 1;
        }

        return index;
    }

    private static string CommandBody(IReadOnlyList<CommandToken> command, int start) =>
        command[start].Raw.StartsWith('"') || command[start].Raw.StartsWith('\'')
            ? command[start].Value : string.Join(' ', command.Skip(start).Select(token => token.Raw));

    private static string ExecutableName(string value)
    {
        var path = value.TrimStart('@').Replace('\\', '/');
        var name = path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) || name.EndsWith(".com", StringComparison.Ordinal) ? name[..^4] : name;
    }

    private static bool EnvironmentAssignment(string value)
    {
        var separator = value.IndexOf('=');
        if (separator < 1 || !char.IsAsciiLetter(value[0]) && value[0] != '_') return false;
        foreach (var character in value.AsSpan(1, separator - 1))
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_') return false;
        }

        return true;
    }

    private static IEnumerable<IReadOnlyList<CommandToken>> Commands(string script)
    {
        var command = new List<CommandToken>();
        var value = new StringBuilder();
        var start = -1;
        var quote = '\0';
        var literalBraces = 0;

        void EndToken(int end)
        {
            if (start < 0) return;
            command.Add(new CommandToken(value.ToString(), script[start..end]));
            value.Clear();
            start = -1;
        }

        for (var i = 0; i < script.Length; i++)
        {
            var character = script[i];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    if (quote == '\'' && i + 1 < script.Length && script[i + 1] == '\'')
                    {
                        value.Append(character);
                        i++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                else if (quote == '"' && (character is '\\' or '`') && i + 1 < script.Length &&
                    (script[i + 1] is '"' or '\\' or '`' || script[i + 1] == '\n'))
                {
                    value.Append(script[++i]);
                }
                else
                {
                    value.Append(character);
                }

                continue;
            }

            if (literalBraces != 0)
            {
                if (character is '\'' or '"') quote = character;
                else if (character == '{') literalBraces++;
                else if (character == '}') literalBraces--;
                value.Append(character);
                continue;
            }

            if (character is '\'' or '"')
            {
                if (start < 0) start = i;
                quote = character;
            }
            else if ((character is '\\' or '`') && i + 1 < script.Length &&
                (char.IsWhiteSpace(script[i + 1]) || script[i + 1] is ';' or '&' or '|' or '(' or ')' or '{' or '}' or '\'' or '"' or '\\' or '`'))
            {
                if (start < 0) start = i;
                value.Append(script[++i]);
            }
            else if (character == '#' && start < 0)
            {
                while (i + 1 < script.Length && script[i + 1] is not ('\r' or '\n')) i++;
            }
            else if (character == '{')
            {
                EndToken(i);
                if (ExecutesScriptBlock(command))
                {
                    if (command.Count != 0) yield return command;
                    command = [];
                }
                else
                {
                    start = i;
                    literalBraces = 1;
                    value.Append(character);
                }
            }
            else if (character is ';' or '&' or '|' or '(' or ')' or '}' or '\r' or '\n')
            {
                EndToken(i);
                if (command.Count != 0)
                {
                    yield return command;
                    command = [];
                }
            }
            else if (char.IsWhiteSpace(character))
            {
                EndToken(i);
            }
            else
            {
                if (start < 0) start = i;
                value.Append(character);
            }
        }

        EndToken(script.Length);
        if (command.Count != 0) yield return command;
    }

    private static bool ExecutesScriptBlock(IReadOnlyList<CommandToken> command) =>
        command.Count == 0 || ExecutableName(command[0].Value) is "." or "%" or "foreach-object" or "where-object" or "?" or
            "invoke-command" or "start-job" or "start-threadjob" or "foreach" or "for" or "while" or "until" or "do" or
            "if" or "elseif" or "else" or "then" or "switch" or "function" or "filter" or "try" or "catch" or "finally" or
            "begin" or "process" or "end" or "pwsh" or "powershell";

    private readonly record struct CommandToken(string Value, string Raw);

    internal static string CanonicalChord(string chord)
    {
        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        string? key = null;
        foreach (var token in chord.Split('+', StringSplitOptions.TrimEntries))
        {
            var part = token.ToLowerInvariant();
            switch (part)
            {
                case "ctrl" or "control": modifiers.Add("ctrl"); break;
                case "alt": modifiers.Add("alt"); break;
                case "shift": modifiers.Add("shift"); break;
                case "win": modifiers.Add("win"); break;
                default:
                    key = part switch
                    {
                        "del" => "delete",
                        "ins" => "insert",
                        "return" => "enter",
                        "esc" => "escape",
                        ['f', .. var number] when int.TryParse(number, out var function) && function is >= 1 and <= 24 => $"f{function}",
                        _ => part,
                    };
                    break;
            }
        }

        return (modifiers.Contains("ctrl") ? "ctrl+" : string.Empty) +
            (modifiers.Contains("alt") ? "alt+" : string.Empty) +
            (modifiers.Contains("shift") ? "shift+" : string.Empty) +
            (modifiers.Contains("win") ? "win+" : string.Empty) + key;
    }

    private static bool ActivatesTarget(ActionDescriptor action)
    {
        if (action.Keys is { Length: > 0 } keys)
        {
            foreach (var chord in keys.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (CanonicalChord(chord) is "enter" or "space")
                {
                    return true;
                }
            }

            return false;
        }

        return action.Action is "click" or "double_click" or "right_click" or "invoke" or "toggle" or
            "select" or "expand" or "collapse" or "check" or "uncheck" or "close" or "drag";
    }

    /// <summary>A drop target that is the Recycle Bin, a trash can or similar, in English names. Best effort, like the rest of this policy.</summary>
    internal static bool DropTargetIsBin(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name.Contains("recycle bin", StringComparison.OrdinalIgnoreCase) || name.Contains("trash", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("rubbish", StringComparison.OrdinalIgnoreCase) || name.Contains("wastebasket", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string text, string[] fragments)
    {
        foreach (var f in fragments)
        {
            if (text.Contains(f, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
