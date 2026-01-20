using System;
using System.Security.Cryptography;
using System.Text;

namespace Shoots.Contracts.Core;

/// <summary>
/// Deterministic hashing utilities.
/// </summary>
public static class HashTools
{
    public static string ComputeSha256Hash(string value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));

        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
