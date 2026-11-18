using OmniWin.Core;

namespace OmniWin.Safety.Tests;

public sealed class DeletePolicyTests
{
    [Theory]
    [InlineData("Delete")]
    [InlineData("Remove item")]
    [InlineData("Permanently delete")]
    [InlineData("Move to the recycle bin")]
    [InlineData("Empty trash")]
    [InlineData("Clear browsing data")]
    [InlineData("Clear all history")]
    [InlineData("Reset this PC")]
    [InlineData("Factory reset")]
    [InlineData("Uninstall")]
    public void Destructive_names_are_denied_for_activation(string name)
    {
        var action = Action("click", target: Target(name: name));
        Assert.NotNull(Forbidden(action));
    }

    [Theory]
    [InlineData("Save")]
    [InlineData("Send")]
    [InlineData("Submit")]
    [InlineData("Approve payment")]
    [InlineData("Undelete")]
    [InlineData("Removal report")]
    public void Ordinary_action_names_are_allowed(string name) =>
        Assert.Null(Forbidden(Action("click", target: Target(name: name))));

    [Theory]
    [InlineData("DeleteButton")]
    [InlineData("btn-remove")]
    [InlineData("TrashAction")]
    [InlineData("UninstallMenuItem")]
    public void Destructive_ids_are_denied(string id) =>
        Assert.NotNull(Forbidden(Action("invoke", target: Target(name: "Unnamed", id: id))));

    [Theory]
    [InlineData("type")]
    [InlineData("set_value")]
    [InlineData("clear")]
    [InlineData("focus")]
    [InlineData("scroll_into_view")]
    public void Editing_a_field_named_remove_is_allowed(string action) =>
        Assert.Null(Forbidden(Action(action, target: Target(name: "Remove", role: "edit", id: "RemoveField"), value: "ordinary text")));

    [Theory]
    [InlineData("enter")]
    [InlineData("space")]
    [InlineData("return")]
    [InlineData("ctrl+a enter")]
    public void Keyboard_activation_of_a_delete_target_is_denied(string keys) =>
        Assert.NotNull(Forbidden(Action("keys", Target(name: "Delete"), keys: keys)));

    [Fact]
    public void Editing_keys_on_a_field_named_remove_are_allowed() =>
        Assert.Null(Forbidden(Action("keys", Target(name: "Remove", role: "edit"), keys: "ctrl+a backspace")));

    [Theory]
    [InlineData("shift+delete")]
    [InlineData("DELETE+SHIFT")]
    [InlineData("shift+del")]
    [InlineData("control+shift+delete")]
    [InlineData("shift+control+Del")]
    [InlineData("ctrl+shift+ctrl+delete")]
    [InlineData("ctrl+a\tSHIFT+DEL")]
    public void Delete_chords_are_checked_independently_of_order_and_spelling(string keys) =>
        Assert.NotNull(DeletePolicy.WhyKeysForbidden(keys, "notepad.exe"));

    [Theory]
    [InlineData("delete")]
    [InlineData("Del")]
    [InlineData("ctrl+d")]
    [InlineData("ctrl+delete")]
    [InlineData("CONTROL+D")]
    [InlineData("ctrl+a\ndelete")]
    public void Explorer_delete_shortcuts_are_denied(string keys) =>
        Assert.NotNull(DeletePolicy.WhyKeysForbidden(keys, "EXPLORER.EXE"));

    [Theory]
    [InlineData("delete")]
    [InlineData("ctrl+d")]
    [InlineData("ctrl+delete")]
    [InlineData("ctrl+a backspace")]
    [InlineData("alt+f4")]
    [InlineData("ctrl+enter")]
    public void Ordinary_editing_and_closing_shortcuts_are_allowed(string keys) =>
        Assert.Null(DeletePolicy.WhyKeysForbidden(keys, "notepad.exe"));

    [Theory]
    [InlineData("del document.txt")]
    [InlineData("rm -rf folder")]
    [InlineData("Remove-Item example.txt")]
    [InlineData("echo hello; rm item")]
    [InlineData("echo hello\r\ndel item")]
    [InlineData("git clean -fd")]
    [InlineData("git rm item")]
    [InlineData("format D:")]
    [InlineData("diskpart")]
    public void Delete_commands_are_recognized_without_executing_them(string text) =>
        Assert.True(DeletePolicy.IsDeleteCommand(text));

    [Theory]
    [InlineData("echo hello")]
    [InlineData("git status")]
    [InlineData("Write-Output 'remove'")]
    [InlineData("formatting report")]
    public void Ordinary_commands_are_allowed(string text) =>
        Assert.Null(Forbidden(Action("type", process: "pwsh.exe", value: text)));

    [Fact]
    public void Terminals_are_allowed_except_for_delete_commands()
    {
        Assert.Null(Forbidden(Action("type", process: "WindowsTerminal.exe", value: "echo hello")));
        Assert.NotNull(Forbidden(Action("type", process: "WindowsTerminal.exe", value: "rm item")));
    }

    [Fact]
    public void Raw_delete_text_in_a_document_is_allowed() =>
        Assert.Null(Forbidden(Action("type", Target(role: "edit"), value: "rm item")));

    [Fact]
    public void Redacted_delete_command_flag_is_enforced() =>
        Assert.NotNull(Forbidden(Action("type", process: "pwsh.exe", value: "<21 chars>") with { ContainsDeleteCommand = true }));

    [Fact]
    public void Explorer_address_bar_and_run_are_command_targets()
    {
        Assert.True(DeletePolicy.IsCommandTarget(Target(process: "explorer.exe", name: "Address: Desktop"), "explorer.exe", "Desktop"));
        Assert.True(DeletePolicy.IsCommandTarget(Target(process: "explorer.exe", role: "edit"), "explorer.exe", "Run"));
        Assert.True(DeletePolicy.IsCommandTarget(Target(process: "explorer.exe", role: "edit") with
        {
            Path = [new SelectorSegment("group", null, null, "Address Band Root", null)],
        }, "explorer.exe", "Desktop"));
    }

    [Fact]
    public void Explorer_search_and_rename_fields_are_not_command_targets()
    {
        var target = Target(process: "explorer.exe", name: "Search Downloads", role: "edit");
        Assert.False(DeletePolicy.IsCommandTarget(target, target.Process, target.WindowTitle));
        Assert.Null(Forbidden(Action("type", target, value: "rm item")));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("ctrl+delete")]
    [InlineData("ctrl+d")]
    public void Common_file_dialog_views_get_the_same_delete_shortcut_checks(string keys)
    {
        var file = Target(name: "report.txt", role: "listitem") with
        {
            Path =
            [
                new("pane", null, null, "DUIViewWndClassName", null),
                new("pane", null, null, "SHELLDLL_DefView", null),
                new("list", "ItemsView", "Items View", "DirectUIHWND", null),
                new("listitem", null, "report.txt", null, null),
            ],
        };
        Assert.True(DeletePolicy.IsFileListTarget(file));
        Assert.NotNull(Forbidden(Action("keys", file, keys: keys)));
    }

    [Fact]
    public void Shell_view_evidence_does_not_depend_on_English_labels()
    {
        var file = Target(role: "dataitem", name: "Bericht.txt") with
        {
            Path = [new("pane", null, "Dateien", "SHELLDLL_DefView", null)],
        };
        Assert.NotNull(Forbidden(Action("keys", file, keys: "delete")));
    }

    [Fact]
    public void Items_view_id_and_DirectUI_class_identify_a_hosted_file_list()
    {
        var file = Target(role: "listitem") with
        {
            Path = [new("list", "ItemsView", null, "DirectUIHWND", null)],
        };
        Assert.True(DeletePolicy.IsFileListTarget(file));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("ctrl+delete")]
    public void Ordinary_lists_and_text_edits_keep_their_editing_shortcuts(string keys)
    {
        var ordinaryList = Target(role: "listitem", name: "Choice") with
        {
            Path = [new("window", null, "Options", "#32770", null), new("list", null, "Choices", "DirectUIHWND", null)],
        };
        Assert.False(DeletePolicy.IsFileListTarget(ordinaryList));
        Assert.Null(Forbidden(Action("keys", ordinaryList, keys: keys)));
        Assert.Null(Forbidden(Action("keys", Target(process: "explorer.exe", role: "edit", name: "File name"), keys: keys)));
    }

    [Fact]
    public void File_type_combo_items_are_not_treated_as_files()
    {
        var fileType = Target(role: "listitem", name: "Text documents") with
        {
            Path = [new("pane", null, null, "SHELLDLL_DefView", null), new("combobox", null, "Save as type", null, null)],
        };
        Assert.False(DeletePolicy.IsFileListTarget(fileType));
        Assert.Null(Forbidden(Action("keys", fileType, keys: "delete")));
    }

    [Theory]
    [InlineData("sudo rm -rf folder")]
    [InlineData("sudo -u root /bin/rm folder")]
    [InlineData("env MODE=test sudo rm folder")]
    [InlineData("cmd /c del item.txt")]
    [InlineData("cmd.exe /d /s /c \"echo ready & del item.txt\"")]
    [InlineData("pwsh -c Remove-Item item.txt")]
    [InlineData("powershell.exe -NoProfile -Command \"Remove-Item item.txt\"")]
    [InlineData("bash -c 'rm item.txt'")]
    [InlineData("bash -lc \"echo ready; rm item.txt\"")]
    [InlineData("wsl rm item.txt")]
    [InlineData("wsl.exe -d Ubuntu --exec /bin/rm item.txt")]
    [InlineData("wsl -e sh -c 'rm item.txt'")]
    [InlineData("find . -type f -delete")]
    [InlineData("find . -exec rm '{}' \\;")]
    [InlineData("find . -print0 | xargs -0 rm")]
    [InlineData("xargs -I '{}' rm '{}'")]
    [InlineData("robocopy C:\\Source C:\\Destination /MIR")]
    [InlineData("robocopy C:\\Source C:\\Destination /e /purge")]
    [InlineData("git.exe -C repo clean -fd")]
    [InlineData("git --work-tree repo rm file.txt")]
    [InlineData("\"C:\\Windows\\System32\\cmd.exe\" /c del file.txt")]
    public void Common_delete_wrappers_and_destructive_options_are_detected(string command) =>
        Assert.True(DeletePolicy.IsDeleteCommand(command));

    [Theory]
    [InlineData("for f in *; do rm $f; done")]
    [InlineData("for f in *\ndo sudo rm \"$f\"\ndone")]
    [InlineData("if [ -f x ]; then rm x; fi")]
    [InlineData("if test -f x; then echo ready; else rm x; fi")]
    [InlineData("if rm x; then echo removed; fi")]
    [InlineData("cmd /c for %f in (*) do del %f")]
    [InlineData("for /f %f in ('dir /b') do call del %f")]
    [InlineData("if exist x del x")]
    [InlineData("if not exist x del y")]
    [InlineData("if defined TEMP del x")]
    [InlineData("if errorlevel 1 del x")]
    [InlineData("if /I \"%MODE%\"==\"clean\" del x")]
    [InlineData("if 1 EQU 1 del x")]
    [InlineData("gci | % { rm $_ }")]
    [InlineData("Get-ChildItem | ForEach-Object { Remove-Item $_ }")]
    [InlineData("Get-ChildItem|%{Remove-Item $_}")]
    [InlineData("foreach ($f in Get-ChildItem) { Remove-Item $f }")]
    [InlineData("& { Remove-Item x }")]
    [InlineData("start cmd /c del x")]
    [InlineData("start \"\" cmd /c \"del x\"")]
    [InlineData("start /b \"Cleanup\" cmd /c del x")]
    [InlineData("start /d C:\\Work /wait \"\" cmd /c del x")]
    [InlineData("call cmd /c del x")]
    [InlineData("@call cmd /c del x")]
    [InlineData("Format-Volume -DriveLetter D")]
    [InlineData("Clear-Disk -Number 2")]
    [InlineData("Remove-Partition -DiskNumber 2 -PartitionNumber 1")]
    [InlineData("pwsh -c \"Get-Disk | ForEach-Object { Clear-Disk $_ }\"")]
    public void Delete_commands_inside_common_control_constructs_are_detected(string command) =>
        Assert.True(DeletePolicy.IsDeleteCommand(command));

    [Theory]
    [InlineData("for f in *; do echo rm $f; done")]
    [InlineData("if [ -f x ]; then echo rm x; fi")]
    [InlineData("if exist x echo del x")]
    [InlineData("if not exist x call echo del x")]
    [InlineData("for %f in (*) do echo del %f")]
    [InlineData("Get-ChildItem | ForEach-Object { Write-Output 'rm file' }")]
    [InlineData("gci | % { echo \"literal ; rm x\" }")]
    [InlineData("Write-Output { rm x; Remove-Item y }")]
    [InlineData("echo { rm x }")]
    [InlineData("echo \"for f in *; do rm $f; done\"")]
    [InlineData("echo 'gci | % { rm $_ }'")]
    [InlineData("Write-Output @{ Example = 'Remove-Item x'; Mode = 'literal' }")]
    [InlineData("# for f in *; do rm $f; done")]
    [InlineData("start \"\" notepad.exe")]
    [InlineData("call cmd /c echo hello")]
    [InlineData("Get-Volume")]
    [InlineData("Get-Disk")]
    [InlineData("Get-Partition")]
    [InlineData("echo Format-Volume")]
    public void Control_constructs_and_data_blocks_preserve_ordinary_commands(string command) =>
        Assert.False(DeletePolicy.IsDeleteCommand(command));

    [Theory]
    [InlineData("echo 'rm item.txt'")]
    [InlineData("echo \"literal ; rm item.txt\"")]
    [InlineData("Write-Output '; rm item.txt'")]
    [InlineData("cmd /c echo hello")]
    [InlineData("pwsh -c \"Write-Output 'rm item.txt'\"")]
    [InlineData("bash -c 'echo hello'")]
    [InlineData("wsl echo hello")]
    [InlineData("find . -name -delete -print")]
    [InlineData("find . -exec echo rm '{}' \\;")]
    [InlineData("xargs echo rm")]
    [InlineData("robocopy C:\\Source C:\\Destination /e")]
    [InlineData("robocopy C:\\Source C:\\Destination /move")]
    [InlineData("git -C repo status")]
    [InlineData("# a comment; rm item.txt")]
    public void Quoted_examples_comments_and_ordinary_wrapper_commands_are_allowed(string command) =>
        Assert.False(DeletePolicy.IsDeleteCommand(command));

    [Theory]
    [InlineData("PS C:\\Work> Remove-Item file.txt")]
    [InlineData("C:\\Work>del file.txt")]
    [InlineData("user@host:~/work$ rm file.txt")]
    [InlineData("$ sudo rm file.txt")]
    public void Current_terminal_lines_can_be_checked_with_their_prompt(string line) =>
        Assert.True(DeletePolicy.IsDeleteCommandLine(line));

    [Theory]
    [InlineData("enter", true)]
    [InlineData("return", true)]
    [InlineData("ctrl+enter", true)]
    [InlineData("ctrl+a enter", true)]
    [InlineData("shift+enter", false)]
    [InlineData("ctrl+a backspace", false)]
    public void Command_submission_shortcuts_are_identified(string keys, bool submits) =>
        Assert.Equal(submits, DeletePolicy.IsCommandSubmission(keys));

    [Theory]
    [InlineData("Code.exe", "xterm-helper-textarea")]
    [InlineData("Cursor.exe", "xterm-screen")]
    [InlineData("devenv.exe", "Microsoft.VisualStudio.TerminalControl")]
    [InlineData("idea64.exe", "TerminalTextArea")]
    [InlineData("powershell_ise.exe", "ConsolePane")]
    public void Embedded_terminal_controls_are_command_targets(string process, string className)
    {
        var target = Target(process: process, role: "edit") with
        {
            Path = [new("pane", null, null, className, null)],
        };
        Assert.True(DeletePolicy.IsCommandTarget(target, process, "Editor"));
        Assert.NotNull(Forbidden(Action("type", target, value: "sudo rm item.txt")));
    }

    [Fact]
    public void Editor_terminal_container_evidence_is_accepted()
    {
        var target = Target(process: "Code.exe", role: "edit") with
        {
            Path = [new("group", null, "Terminal", null, null), new("edit", null, "Terminal input", null, null)],
        };
        Assert.True(DeletePolicy.IsCommandTarget(target, target.Process, "Editor"));
    }

    [Theory]
    [InlineData("Code.exe")]
    [InlineData("devenv.exe")]
    [InlineData("idea64.exe")]
    [InlineData("powershell_ise.exe")]
    public void Ordinary_editor_documents_remain_editable(string process)
    {
        var target = Target(process: process, role: "document", name: "TerminalControl.cs", id: "C:\\Source\\TerminalControl.cs") with
        {
            Path = [new("group", null, "Editor", "EditorPane", null), new("document", null, "TerminalControl.cs", "TextEditor", null)],
        };
        Assert.False(DeletePolicy.IsCommandTarget(target, process, "Editor"));
        Assert.Null(Forbidden(Action("type", target, value: "rm item.txt")));
    }

    [Fact]
    public void Extra_words_use_word_boundaries()
    {
        Assert.True(DeletePolicy.IsDeleteName("Purge item", ["purge"]));
        Assert.False(DeletePolicy.IsDeleteName("Spurged report", ["purge"]));
    }

    [Theory]
    [InlineData("consent.exe")]
    [InlineData("LogonUI.exe")]
    [InlineData("CredentialUIBroker.exe")]
    public void Secure_and_sign_in_processes_are_denied(string process) =>
        Assert.NotNull(Forbidden(Action("type", process: process, value: "hello")));

    private static string? Forbidden(ActionDescriptor action) =>
        DeletePolicy.WhyForbidden(action, DeletePolicy.DefaultBlockedProcesses, []);

    // ---- contract 2.5: drag. A drop onto the Recycle Bin (or a delete action) is a delete.

    [Theory]
    [InlineData("Recycle Bin")]
    [InlineData("Trash")]
    [InlineData("Wastebasket")]
    [InlineData("Delete")]
    [InlineData("Remove item")]
    [InlineData("Empty the recycle bin")]
    public void Dragging_onto_the_recycle_bin_or_a_delete_action_is_denied(string dropName)
    {
        var action = Action("drag", target: Target(name: "report.docx", role: "listitem")) with { DropTarget = Target(name: dropName, role: "listitem") };

        Assert.NotNull(Forbidden(action));
    }

    [Theory]
    [InlineData("Documents")]
    [InlineData("Desktop")]
    [InlineData("Project folder")]
    public void Dragging_onto_an_ordinary_folder_is_allowed(string dropName)
    {
        var action = Action("drag", target: Target(name: "report.docx", role: "listitem")) with { DropTarget = Target(name: dropName, role: "listitem") };

        Assert.Null(Forbidden(action));
    }

    [Fact]
    public void Dragging_a_delete_button_is_denied_like_clicking_it_and_a_drop_target_id_with_trash_is_denied()
    {
        Assert.NotNull(Forbidden(Action("drag", target: Target(name: "Delete", role: "button")) with { DropTarget = Target(name: "Folder") }));
        Assert.NotNull(Forbidden(Action("drag", target: Target(name: "file")) with { DropTarget = Target(name: "Folder", id: "TrashCan") }));
    }

    [Fact]
    public void A_drop_target_matters_only_for_a_drag()
    {
        var action = Action("hover", target: Target(name: "file", role: "listitem")) with { DropTarget = Target(name: "Recycle Bin") };

        Assert.Null(Forbidden(action));
    }

    internal static ElementDescriptor Target(string process = "notepad.exe", string name = "Target", string role = "button", string? id = null) =>
        new("e1", "desktop", process, "Fixture", null, role, name, id, false, true, []);

    internal static ActionDescriptor Action(string action, ElementDescriptor? target = null, string? process = null,
        string? value = null, string? keys = null) =>
        new("desktop_act", "desktop", action, target, process ?? target?.Process ?? "notepad.exe", target?.WindowTitle ?? "Fixture", null, value, keys);
}
