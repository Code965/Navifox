using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Navifox.App.Services;

/// <summary>File locali: il rilascio apre il wizard, senza avviare importazioni.</summary>
public static class ImportFileDrop
{
    public static void Attach(Control surface, Func<object?, bool> acceptsTarget, Func<object?, string, Task> open, Action<string> error)
    {
        DragDrop.SetAllowDrop(surface, true);
        string[] Files(DragEventArgs args) => args.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray() ?? [];
        surface.AddHandler(DragDrop.DragOverEvent, (_, args) =>
        {
            if (!args.DataTransfer.Contains(DataFormat.File)) return;
            try
            {
                var files = Files(args);
                args.DragEffects = files.Length == 1 && File.Exists(files[0]) && ViewModels.ImportWizardViewModel.SupportsFile(files[0]) && acceptsTarget(args.Source) ? DragDropEffects.Copy : DragDropEffects.None;
            }
            catch { args.DragEffects = DragDropEffects.None; }
            args.Handled = true;
        });
        surface.AddHandler(DragDrop.DropEvent, async (_, args) =>
        {
            if (!args.DataTransfer.Contains(DataFormat.File)) return;
            args.Handled = true;
            try
            {
                var files = Files(args);
                if (files.Length != 1) { error("Trascina un solo file alla volta."); return; }
                if (!acceptsTarget(args.Source)) return;
                await open(args.Source, files[0]);
            }
            catch (Exception exception) { error(exception.Message); }
        });
    }
}
