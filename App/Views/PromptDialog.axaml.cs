using Avalonia.Controls;
using Avalonia.Threading;

namespace Navifox.App.Views;

/// <summary>Richiesta di un testo (es. nuovo nome di una tabella).</summary>
public partial class PromptDialog : Window
{
    public PromptDialog() : this("", "", "", "OK") { }

    public PromptDialog(string title, string message, string value, string confirmText, bool isPassword = false)
    {
        InitializeComponent();
        if (isPassword)
        {
            InputBox.PasswordChar = '•';
            InputBox.Classes.Add("revealPasswordButton");
        }
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        InputBox.Text = value;
        ConfirmButton.Content = confirmText;
        ConfirmButton.Click += (_, _) => Close(InputBox.Text);
        CancelButton.Click += (_, _) => Close(null);
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        }, DispatcherPriority.Input);
    }
}
