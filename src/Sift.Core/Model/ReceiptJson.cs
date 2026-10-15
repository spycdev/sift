using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sift.Core.Model;

public static class ReceiptJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Serialize(Receipt receipt) => JsonSerializer.SerializeToUtf8Bytes(receipt, Options);

    public static Receipt Deserialize(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<Receipt>(json, Options) ?? throw new InvalidDataException("The receipt data is empty.");
}
