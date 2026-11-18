using OmniWin.Core;

namespace OmniWin.Desktop.Tests;

/// <summary>D1: set_value / clear on a file dialog's File name box or Explorer's address bar can never be confirmed. Fakes only.</summary>
public sealed class ValueSettingPolicyTests
{
    private static ElementDescriptor El(string process, string windowTitle, string role, string name, string automationId = "", bool password = false) =>
        new("e1", "desktop", process, windowTitle, null, role, name, automationId, password, true, []);

    [Theory]
    [InlineData("Notepad.exe", "Save as", "edit", "File name:")]
    [InlineData("Notepad.exe", "Save as", "combobox", "File name:")]
    [InlineData("chrome.exe", "Open", "edit", "File name:")]
    [InlineData("explorer.exe", "Home - File Explorer", "edit", "Address")]
    [InlineData("explorer.exe", "Downloads", "combobox", "Address: Downloads")]
    [InlineData("explorer.exe", "Downloads", "edit", "", "41477")]
    public void Value_ignoring_targets_are_recognised(string process, string title, string role, string name, string id = "")
    {
        Assert.True(ValueSettingPolicy.IsIgnored(El(process, title, role, name, id)));
    }

    [Theory]
    [InlineData("Notepad.exe", "Untitled - Notepad", "edit", "File name:")]          // not a file dialog
    [InlineData("Notepad.exe", "Save as", "edit", "Search Box")]                     // other box in the dialog
    [InlineData("Notepad.exe", "Save as", "button", "File name:")]                   // not an editable control
    [InlineData("explorer.exe", "Downloads", "edit", "Search Box")]                  // Explorer search box
    [InlineData("notepad.exe", "Untitled", "document", "Text editor")]
    [InlineData("chrome.exe", "Page", "edit", "Address and search bar")]             // a browser's own bar, not Explorer's
    public void Ordinary_targets_are_not_flagged(string process, string title, string role, string name)
    {
        Assert.False(ValueSettingPolicy.IsIgnored(El(process, title, role, name)));
    }

    [Fact]
    public void A_missing_target_or_a_password_box_is_not_flagged()
    {
        Assert.False(ValueSettingPolicy.IsIgnored(null));
        Assert.False(ValueSettingPolicy.IsIgnored(El("Notepad.exe", "Save as", "edit", "File name:", password: true)));
    }

    [Theory]
    [InlineData("set_value", true)]
    [InlineData("clear", true)]
    [InlineData("type", false)]      // typing really goes through the keyboard and is verified by readback
    [InlineData("click", false)]
    public void Only_set_value_and_clear_are_barred_from_confirming_on_a_file_name_box(string action, bool barred)
    {
        var target = El("Notepad.exe", "Save as", "edit", "File name:");
        Assert.Equal(barred, ValueSettingPolicy.MustNotConfirm(action, target));
        Assert.False(ValueSettingPolicy.MustNotConfirm(action, El("Notepad.exe", "Untitled", "document", "Text editor")));
    }

    [Fact]
    public void The_detail_tells_the_model_to_type()
    {
        Assert.Contains("Use type", ValueSettingPolicy.IgnoredDetail);
    }


    // ---- structure rules: any title, any language, any process ----

    private static SelectorSegment Seg(string role, string? id = null, string? name = null, string? cls = null) => new(role, id, name, cls, null);

    private static ElementDescriptor Deep(string process, string windowTitle, string role, string name, string? id, params SelectorSegment[] path) =>
        new("e1", "desktop", process, windowTitle, null, role, name, id, false, true, path);

    [Fact]
    public void An_unusual_english_title_is_still_recognised_by_the_file_name_id()
    {
        Assert.True(ValueSettingPolicy.IsIgnored(Deep("chrome.exe", "Save Image As", "edit", "File name:", "1001",
            Seg("combobox", "1148", "File name:", "ComboBox"), Seg("edit", "1001", "File name:", "Edit"))));
        Assert.True(ValueSettingPolicy.IsIgnored(Deep("notepad.exe", "Open File", "combobox", "File name:", "1148")));
    }

    [Theory]
    [InlineData("Speichern unter", "Dateiname:")]
    [InlineData("名前を付けて保存", "ファイル名:")]
    [InlineData("Enregistrer sous", "Nom du fichier :")]
    public void A_non_english_file_dialog_is_recognised_by_structure(string title, string name)
    {
        var path = new[] { Seg("combobox", "FileNameControlHost", name, "AppControlHost"), Seg("edit", "1001", name, "Edit") };
        Assert.True(ValueSettingPolicy.IsIgnored(Deep("notepad.exe", title, "edit", name, "1001", path)));
        Assert.True(ValueSettingPolicy.MustNotConfirm("set_value", Deep("notepad.exe", title, "edit", name, "1001", path)));
        Assert.True(ValueSettingPolicy.MustNotConfirm("clear", Deep("notepad.exe", title, "edit", name, "1001", path)));
    }

    [Fact]
    public void The_file_name_box_is_recognised_in_a_window_with_no_title()
    {
        Assert.True(ValueSettingPolicy.IsIgnored(Deep("someapp.exe", "", "combobox", "", "FileNameControlHost")));
        Assert.True(ValueSettingPolicy.IsIgnored(Deep("someapp.exe", "", "combobox", "", "1148")));
    }

    [Theory]
    [InlineData("Address Band Root")]
    [InlineData("Breadcrumb Parent")]
    public void A_file_dialogs_own_address_bar_inside_another_process_is_recognised(string bandClass)
    {
        // Not explorer.exe, a German name, no title rule.
        var edit = Deep("notepad.exe", "Speichern unter", "edit", "Adresse", null, Seg("group", null, null, bandClass), Seg("edit", null, "Adresse", "Edit"));
        Assert.True(ValueSettingPolicy.IsIgnored(edit));
        Assert.True(ValueSettingPolicy.MustNotConfirm("set_value", edit));
    }

    [Fact]
    public void The_address_bar_inner_edit_id_is_recognised_in_any_process()
    {
        Assert.True(ValueSettingPolicy.IsIgnored(Deep("notepad.exe", "Open", "edit", "", "41477")));
        Assert.True(ValueSettingPolicy.IsIgnored(Deep("notepad.exe", "Open", "edit", "", null, Seg("edit", "41477"))));
    }

    [Theory]
    [InlineData("edit", "File name:", "txtFileName")]        // a plain app edit that only has the same name
    [InlineData("edit", "Dateiname", null)]
    [InlineData("edit", "Note", "1001")]                     // item 1001 alone is not enough
    [InlineData("combobox", "Search", "searchBox")]
    public void Ordinary_edits_in_ordinary_windows_are_not_flagged_by_the_structure_rules(string role, string name, string? id)
    {
        Assert.False(ValueSettingPolicy.IsIgnored(Deep("notepad.exe", "Untitled - Notepad", role, name, id,
            Seg("group", null, null, "ContentPane"), Seg(role, id, name, "Edit"))));
    }

    [Fact]
    public void Other_address_like_classes_and_browser_bars_are_not_flagged()
    {
        Assert.False(ValueSettingPolicy.IsIgnored(Deep("outlook.exe", "Contacts", "edit", "Address", "address", Seg("group", null, null, "Address Book Panel"))));
        Assert.False(ValueSettingPolicy.IsIgnored(Deep("msedge.exe", "Page", "edit", "Address and search bar", "omnibox")));
    }

    [Theory]
    [InlineData("Start – Datei-Explorer", "Adresse")]        // German
    [InlineData("ホーム - エクスプローラー", "アドレス")]          // Japanese
    [InlineData("Accueil - Explorateur de fichiers", "Adresse")]   // French
    public void A_non_english_explorer_address_bar_is_recognised_by_its_id_or_its_class(string title, string name)
    {
        // By the inner edit's id, with no English word anywhere.
        var byId = Deep("explorer.exe", title, "edit", name, "41477");
        Assert.True(ValueSettingPolicy.IsIgnored(byId));
        Assert.True(ValueSettingPolicy.MustNotConfirm("set_value", byId));
        Assert.True(ValueSettingPolicy.MustNotConfirm("clear", byId));
        Assert.False(ValueSettingPolicy.MustNotConfirm("type", byId));

        // By the class of the band it sits in, with no id at all.
        foreach (var bandClass in new[] { "Address Band Root", "Breadcrumb Parent" })
        {
            var byClass = Deep("explorer.exe", title, "edit", name, null, Seg("group", null, null, bandClass), Seg("edit", null, name, "Edit"));
            Assert.True(ValueSettingPolicy.IsIgnored(byClass));
            Assert.True(ValueSettingPolicy.MustNotConfirm("set_value", byClass));
        }
    }

    [Fact]
    public void Password_boxes_are_never_flagged_by_structure()
    {
        var password = Deep("notepad.exe", "Save as", "edit", "File name:", "1148") with { IsPassword = true };
        Assert.False(ValueSettingPolicy.IsIgnored(password));
    }
}
