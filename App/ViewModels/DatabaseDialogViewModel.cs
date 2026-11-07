using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MosaicDB.Core;
using MosaicDB.Core.Models;

namespace MosaicDB.App.ViewModels;

/// <summary>Finestra «Nuovo database» / «Modifica database» con anteprima SQL.</summary>
public partial class DatabaseDialogViewModel : ObservableObject
{
    private readonly IDialectProvider _provider;
    private readonly DatabaseOptions? _original;
    private readonly IReadOnlyList<CharsetInfo> _charsets;

    public DatabaseDialogViewModel(IDialectProvider provider, IReadOnlyList<CharsetInfo> charsets, DatabaseOptions? original)
    {
        _provider = provider;
        _charsets = charsets;
        _original = original;
        Charsets = new ObservableCollection<string>(new[] { string.Empty }.Concat(charsets.Select(charset => charset.Name)));
        _name = original?.Name ?? string.Empty;
        _selectedCharset = original?.CharacterSet ?? string.Empty;
        RefreshCollations();
        _selectedCollation = original?.Collation ?? string.Empty;
        UpdatePreview();
    }

    public event Action<bool>? CloseRequested;

    public bool IsNew => _original is null;
    public string Title => IsNew ? "Nuovo database" : $"Modifica database · {_original!.Name}";
    public bool CanEditName => IsNew || _provider.SupportsDatabaseRename;
    public bool ShowCollation => _provider.UsesMySqlScriptSyntax;
    public bool CanEditCharset => IsNew || _provider.UsesMySqlScriptSyntax;
    public string CharsetLabel => _provider.UsesMySqlScriptSyntax ? "Set di caratteri" : "Codifica";
    public ObservableCollection<string> Charsets { get; }
    public ObservableCollection<string> Collations { get; } = [];

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _selectedCharset;
    [ObservableProperty] private string _selectedCollation;
    [ObservableProperty] private string _sqlPreview = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public DatabaseOptions Result => new(Name.Trim(), NullIfEmpty(SelectedCharset), NullIfEmpty(SelectedCollation));
    public IReadOnlyList<string> Statements { get; private set; } = [];

    partial void OnNameChanged(string value) => UpdatePreview();
    partial void OnSelectedCharsetChanged(string value)
    {
        RefreshCollations();
        var charset = _charsets.FirstOrDefault(item => item.Name == value);
        SelectedCollation = charset?.DefaultCollation ?? string.Empty;
        UpdatePreview();
    }
    partial void OnSelectedCollationChanged(string value) => UpdatePreview();

    [RelayCommand]
    private void Confirm()
    {
        UpdatePreview();
        if (ErrorMessage.Length > 0) return;
        if (Statements.Count == 0)
        {
            ErrorMessage = "Nessuna modifica da applicare.";
            return;
        }
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    private void RefreshCollations()
    {
        Collations.Clear();
        Collations.Add(string.Empty);
        foreach (var collation in _charsets.FirstOrDefault(item => item.Name == SelectedCharset)?.Collations ?? []) Collations.Add(collation);
    }

    private void UpdatePreview()
    {
        ErrorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(Name))
        {
            Statements = [];
            SqlPreview = "-- Inserisci il nome del database";
            ErrorMessage = "Il nome del database è obbligatorio.";
            return;
        }
        try
        {
            Statements = _original is null ? [_provider.BuildCreateDatabaseSql(Result)] : _provider.BuildAlterDatabaseSql(_original, Result);
            SqlPreview = Statements.Count == 0 ? "-- Nessuna modifica" : string.Join(";\n", Statements) + ";";
        }
        catch (ArgumentException exception)
        {
            Statements = [];
            SqlPreview = string.Empty;
            ErrorMessage = exception.Message;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
