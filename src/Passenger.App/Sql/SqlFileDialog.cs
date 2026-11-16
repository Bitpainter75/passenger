using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Passenger.App.Sql;

/// <summary>Where a database's backup or package is, and of which database: the server's own path for a .bak, which
/// BACKUP and RESTORE read on the server's disk and no panel of this machine can pick, or a .bacpac here, picked in the
/// system's panel. Its button closes with the two; the work itself is the window's.</summary>
public sealed class SqlFileDialog : IdeWindow
{
    private readonly TextBox database = new();
    private readonly TextBox file = new();
    private readonly Button ok;

    /// <param name="names">Whether the database's name is the person's to give (a restore's, an import's target).</param>
    /// <param name="local">For a file on this machine: true to pick one to read, false one to write; null for a server's path.</param>
    public SqlFileDialog(string title, string about, string does, string databaseName, bool names, string path, bool? local)
    {
        FormRows.Scope(this);
        Title = title;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ok = DialogFooter.Default(does);
        ok.Name = "SqlFileOk";
        ok.Click += (_, _) => Close(true);
        database.Text = databaseName;
        database.IsReadOnly = !names;
        file.Text = path;
        database.TextChanged += (_, _) => Paint();
        file.TextChanged += (_, _) => Paint();
        Control field = file;
        if (local is { } reads)
        {
            Button browse = new() { Content = "Browse…", Margin = new Avalonia.Thickness(8, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right };
            browse.Click += (_, _) => IdeErrors.Observe(BrowseAsync(reads));
            field = new DockPanel { Children = { browse, file } };
        }
        StackPanel fields = new() { Spacing = 10, Children = { FormRows.Of("Database:", database), FormRows.Of(local is null ? "File on the server:" : "File:", field) } };
        Content = DialogFooter.Headed(title, new TextBlock { Classes = { StyleClasses.Muted }, Text = about, TextWrapping = TextWrapping.Wrap, FontSize = 12 }, fields,
            DialogFooter.OkCancel(ok, () => Close(false)));
        Opened += (_, _) => (names ? database : file).Focus();
        Paint();
    }

    /// <summary>The database named.</summary>
    public string Database
    {
        get => (database.Text ?? string.Empty).Trim();
        set => database.Text = value;
    }

    /// <summary>The file named.</summary>
    public string File
    {
        get => (file.Text ?? string.Empty).Trim();
        set => file.Text = value;
    }

    private void Paint() => ok.IsEnabled = Database.Length > 0 && File.Length > 0;

    private async Task BrowseAsync(bool reads)
    {
        FilePickerFileType packages = new("Data-tier application packages") { Patterns = ["*.bacpac"] };
        if (reads)
        {
            IReadOnlyList<IStorageFile> picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Title, FileTypeFilter = [packages, FilePickerFileTypes.All] });
            if (picked.Count == 1 && picked[0].TryGetLocalPath() is { } path)
            {
                file.Text = path;
            }
        }
        else if (await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = Title, FileTypeChoices = [packages], DefaultExtension = "bacpac", SuggestedFileName = $"{Database}.bacpac" })
            is { } saved && saved.TryGetLocalPath() is { } to)
        {
            file.Text = to;
        }
    }
}
