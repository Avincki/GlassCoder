using System.Windows;

namespace GlassCoder.Wpf.Views;

/// <summary>
/// Asks for the passphrase that protects an exported configuration's API keys.
/// <para>
/// The one dialog with logic in its code-behind, and deliberately: <c>PasswordBox.Password</c> is
/// not a dependency property, precisely so a passphrase cannot be left sitting in a bound view
/// model where a screenshot, a log or a crash dump might find it. Binding it would mean
/// re-implementing that hole. What is here is presentation only - two boxes must agree - and the
/// value leaves through <see cref="Passphrase"/> at the moment the caller asks for it.
/// </para>
/// </summary>
public partial class PassphraseWindow : Window
{
    /// <summary>Creates the dialog.</summary>
    /// <param name="title">Window title and heading.</param>
    /// <param name="message">What the passphrase is for.</param>
    /// <param name="confirm">Whether the passphrase must be typed twice.</param>
    public PassphraseWindow(string title, string message, bool confirm)
    {
        InitializeComponent();

        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;

        ConfirmPanel.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = confirm
            ? "Leave both boxes empty to write the file without any API keys. There is no way to recover a " +
              "passphrase you forget - the keys in the file become unreadable, and you would enter them again by hand."
            : "Leave empty to import the settings without the API keys.";

        Loaded += (_, _) => FirstBox.Focus();
    }

    /// <summary>What was typed. Empty means "carry on without the keys".</summary>
    public string Passphrase { get; private set; } = string.Empty;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        string first = FirstBox.Password;

        if (ConfirmPanel.Visibility == Visibility.Visible && !string.Equals(first, SecondBox.Password, System.StringComparison.Ordinal))
        {
            ErrorText.Text = "The two passphrases do not match.";
            SecondBox.Clear();
            SecondBox.Focus();
            return;
        }

        Passphrase = first;
        DialogResult = true;
    }
}
