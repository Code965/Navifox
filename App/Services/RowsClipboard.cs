using System.Text;
using Avalonia.Input;
using Navifox.Core;

namespace Navifox.App.Services;

public static class RowsClipboard
{
    public static DataFormat<byte[]> Format { get; } = DataFormat.CreateBytesApplicationFormat(GridRowsClipboard.Format);

    public static DataTransfer Create(GridRowsClipboard rows)
    {
        rows.Validate();
        var item = new DataTransferItem();
        item.SetText(rows.ToTsv());
        item.Set(Format, Encoding.UTF8.GetBytes(rows.Serialize()));
        var data = new DataTransfer();
        data.Add(item);
        return data;
    }

    public static async Task<GridRowsClipboard> ReadAsync(IAsyncDataTransfer data)
    {
        var bytes = await data.TryGetValueAsync(Format) ?? throw new FormatException("Le righe degli appunti non sono disponibili. Copia nuovamente le righe.");
        return GridRowsClipboard.Deserialize(Encoding.UTF8.GetString(bytes).TrimEnd('\0').TrimStart('\uFEFF'));
    }
}
