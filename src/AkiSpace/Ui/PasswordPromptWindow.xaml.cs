using System.Windows;

namespace AkiSpace.Ui;

public partial class PasswordPromptWindow : ChromeWindow
{
    public string? Password { get; private set; }

    public PasswordPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PbPassword.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Password = PbPassword.Password;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>Modal prompt used by the connection controller's password delegate.</summary>
    public static string? Prompt()
    {
        var window = new PasswordPromptWindow();
        return window.ShowDialog() == true ? window.Password : null;
    }
}
