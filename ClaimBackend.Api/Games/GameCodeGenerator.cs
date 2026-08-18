using System.Security.Cryptography;

namespace ClaimBackend.Api.Games;

public static class GameCodeGenerator
{
    /// <summary>
    /// Codes get read aloud and typed in by hand, so I/1 and O/0 are left out to keep them
    /// unambiguous.
    /// </summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private const int CodeLength = 6;

    public static string Next() => RandomNumberGenerator.GetString(Alphabet, CodeLength);

    /// <summary>Players type codes however they like; the stored form is upper case.</summary>
    public static string Normalize(string code) => code.Trim().ToUpperInvariant();
}
