using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace Navifox.App.ViewModels;

/// <summary>Notifica in basso a destra (operazioni lunghe completate o non riuscite).</summary>
public sealed record ToastMessage(string Title, string Message, bool IsError)
{
    public string Glyph => IsError ? "x" : "check";
}

public partial class MainWindowViewModel
{
    public ObservableCollection<ToastMessage> Toasts { get; } = [];

    /// <summary>Mostra una notifica per qualche secondo (massimo quattro alla volta).</summary>
    public void Notify(string title, string message, bool isError = false)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            var toast = new ToastMessage(title, message, isError);
            Toasts.Add(toast);
            while (Toasts.Count > 4) Toasts.RemoveAt(0);
            await Task.Delay(isError ? 9000 : 5500);
            Toasts.Remove(toast);
        });
    }

    [RelayCommand]
    private void DismissToast(ToastMessage? toast)
    {
        if (toast is not null) Toasts.Remove(toast);
    }
}
