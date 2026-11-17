using System.Security.Cryptography;
using System.Text;

namespace SmartCon.Agent.Services;

public static class AgentHashing
{
    public static string Sha256Hex(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));

        byte[] bytes;
#if NET8_0_OR_GREATER
        bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
#else
        using (var sha = SHA256.Create())
        {
            bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        }
#endif
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            builder.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}