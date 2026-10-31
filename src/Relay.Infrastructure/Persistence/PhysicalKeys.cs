using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Relay.Infrastructure.Persistence;

public static class PhysicalKeys
{
    public static string Component(string value) => Uri.EscapeDataString(value);
    public static string Number(ulong value) => value.ToString("D20", CultureInfo.InvariantCulture);
    public static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    public static int Shard(string id, int count)
    {
        if (count is not (16 or 32)) throw new ArgumentOutOfRangeException(nameof(count));
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(id))) % count);
    }
}
