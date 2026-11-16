using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Sql;

namespace Passenger.App.Sql;

/// <summary>Connect to SQL Server: the server's address as its tools take one, a name for it in the window, how to
/// sign in, the login's user and password, and whether a certificate no authority signed is trusted, as a development
/// server's needs. Connect tries it while the dialog stays, saying the server's own words where it fails, and closes
/// once connected. The password goes to the connection alone and is kept in no file.</summary>
public sealed class SqlConnectDialog : IdeWindow
{
    private const string Login = "SQL Server login";
    private const string Integrated = "Windows or Kerberos, as you";

    private readonly Func<SqlConnectionInfo, string?, Task<string?>> connect;
    private readonly TextBox server = new() { PlaceholderText = "host, host,1433 or host\\instance" };
    private readonly TextBox name = new() { PlaceholderText = "Optional: what the window calls it" };
    private readonly ComboBox signIn = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { Login, Integrated } };
    private readonly TextBox user = new();
    private readonly TextBox password = new() { PasswordChar = '•' };
    private readonly CheckBox trust = new() { Content = "Trust the server's certificate" };
    private readonly TextBlock problem = new() { Classes = { StyleClasses.Error }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button ok = DialogFooter.Default("Connect");
    private bool connecting;

    /// <param name="before">The server to connect to again, its password alone to give; null for a new one.</param>
    /// <param name="connect">Connects, answering null once connected, else why not.</param>
    public SqlConnectDialog(SqlConnectionInfo? before, Func<SqlConnectionInfo, string?, Task<string?>> connect)
    {
        this.connect = connect;
        ok.Name = "SqlConnectOk";
        trust.Name = "SqlTrust";
        FormRows.Scope(this);
        Title = "Connect to SQL Server";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        server.Text = before?.Server ?? string.Empty;
        name.Text = before?.Name ?? string.Empty;
        user.Text = before?.User ?? string.Empty;
        trust.IsChecked = before?.TrustCertificate ?? false;
        signIn.SelectedIndex = before?.SignIn == SqlSignIn.Integrated ? 1 : 0;
        Grid userRow = FormRows.Of("User:", user);
        Grid passwordRow = FormRows.Of("Password:", password);
        signIn.SelectionChanged += (_, _) => Paint();
        server.TextChanged += (_, _) => Paint();
        user.TextChanged += (_, _) => Paint();
        ok.Click += (_, _) => IdeErrors.Observe(ConnectAsync());
        StackPanel fields = new()
        {
            Spacing = 10,
            Children = { FormRows.Of("Server:", server), FormRows.Of("Name:", name), FormRows.Of("Sign in:", signIn), userRow, passwordRow, FormRows.Of(null, trust) },
        };
        Content = DialogFooter.Page(fields, DialogFooter.OkCancel(ok, () => Close(false), problem));
        Opened += (_, _) => (before is null ? server : before.SignIn == SqlSignIn.Login ? password : (Control)ok).Focus();
        Paint();

        void Paint()
        {
            bool login = signIn.SelectedIndex == 0;
            userRow.IsVisible = login;
            passwordRow.IsVisible = login;
            ok.IsEnabled = !connecting && !string.IsNullOrWhiteSpace(server.Text) && (!login || !string.IsNullOrWhiteSpace(user.Text));
        }
    }

    /// <summary>The server as the dialog has it.</summary>
    public SqlConnectionInfo Info => new((name.Text ?? string.Empty).Trim(), (server.Text ?? string.Empty).Trim(),
        signIn.SelectedIndex == 1 ? SqlSignIn.Integrated : SqlSignIn.Login, (user.Text ?? string.Empty).Trim(), trust.IsChecked == true);

    /// <summary>Why the last try did not connect, null while it says nothing.</summary>
    public string? Problem => problem.Text is { Length: > 0 } said ? said : null;

    /// <summary>Fills the dialog as a person would, then connects as Connect does.</summary>
    public Task FillAsync(SqlConnectionInfo info, string? secret)
    {
        server.Text = info.Server;
        name.Text = info.Name;
        user.Text = info.User;
        password.Text = secret;
        trust.IsChecked = info.TrustCertificate;
        signIn.SelectedIndex = info.SignIn == SqlSignIn.Integrated ? 1 : 0;
        return ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (connecting)
        {
            return;
        }
        connecting = true;
        ok.IsEnabled = false;
        problem.Text = string.Empty;
        string? refused = await connect(Info, signIn.SelectedIndex == 0 ? password.Text ?? string.Empty : null);
        connecting = false;
        if (refused is null)
        {
            Close(true);
            return;
        }
        problem.Text = refused;
        ok.IsEnabled = true;
    }
}
