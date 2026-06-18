using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace MediaServer.Api.Jellyfin.Auth;

/// <summary>Hashes and verifies the short numeric PIN with a slow, memory-hard algorithm (argon2id).</summary>
public interface IPinHasher
{
    string Hash(string pin);

    bool Verify(string pin, string encodedHash);
}

/// <summary>
/// argon2id PIN hasher. Parameters follow the OWASP minimum for argon2id (19 MiB, t=2, p=1) — the PIN
/// space is tiny so the cost is what keeps an exfiltrated hash from being brute-forced offline. The
/// salt and parameters are embedded in the encoded string so <see cref="Verify"/> is self-describing.
/// </summary>
public sealed class Argon2idPinHasher : IPinHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MemoryKib = 19 * 1024;
    private const int Iterations = 2;
    private const int Parallelism = 1;

    public string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(pin, salt, MemoryKib, Iterations, Parallelism);
        return string.Join('$',
            "argon2id",
            $"m={MemoryKib},t={Iterations},p={Parallelism}",
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public bool Verify(string pin, string encodedHash)
    {
        var parts = encodedHash.Split('$');
        if (parts.Length != 4 || parts[0] != "argon2id")
        {
            return false;
        }

        var parameters = ParseParameters(parts[1]);
        if (parameters is not var (memory, iterations, parallelism))
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Derive(pin, salt, memory, iterations, parallelism, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string pin, byte[] salt, int memoryKib, int iterations, int parallelism, int outputBytes = HashSize)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(pin))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };

        return argon2.GetBytes(outputBytes);
    }

    private static (int Memory, int Iterations, int Parallelism)? ParseParameters(string raw)
    {
        int? memory = null, iterations = null, parallelism = null;
        foreach (var pair in raw.Split(','))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2 || !int.TryParse(kv[1], out var value))
            {
                return null;
            }

            switch (kv[0])
            {
                case "m": memory = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
            }
        }

        return memory is { } m && iterations is { } t && parallelism is { } p ? (m, t, p) : null;
    }
}
