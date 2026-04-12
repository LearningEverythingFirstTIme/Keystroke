using System.Buffers.Binary;
using System.Security.Cryptography;

// ---------------------------------------------------------------
// Keystroke License Key Generator
//
// Usage:
//   dotnet run -- init           Generate new ECDSA P-256 keypair
//   dotnet run -- generate [N]   Generate N license keys (default 1)
//   dotnet run -- verify <key>   Verify a license key
//
// The private key is stored in private-key.pem (NEVER share this).
// The public key bytes are printed for embedding in LicenseService.cs.
// ---------------------------------------------------------------

const string PrivateKeyFile = "private-key.pem";
const string KeyPrefix = "KS-";
const int PayloadLength = 14;

if (args.Length == 0)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run -- init             Generate new ECDSA P-256 keypair");
    Console.WriteLine("  dotnet run -- generate [N]     Generate N Pro license keys (default 1)");
    Console.WriteLine("  dotnet run -- verify <key>     Verify a license key");
    return;
}

switch (args[0].ToLowerInvariant())
{
    case "init":
        InitKeypair();
        break;
    case "generate":
        var count = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 1;
        GenerateKeys(count);
        break;
    case "verify":
        if (args.Length < 2)
        {
            Console.WriteLine("Error: provide a license key to verify.");
            return;
        }
        VerifyKey(args[1]);
        break;
    default:
        Console.WriteLine($"Unknown command: {args[0]}");
        break;
}

void InitKeypair()
{
    if (File.Exists(PrivateKeyFile))
    {
        Console.WriteLine($"WARNING: {PrivateKeyFile} already exists. Delete it first to regenerate.");
        return;
    }

    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var pem = ecdsa.ExportECPrivateKeyPem();
    File.WriteAllText(PrivateKeyFile, pem);
    Console.WriteLine($"Private key saved to: {PrivateKeyFile}");
    Console.WriteLine("KEEP THIS SECRET. Never commit or share it.\n");

    PrintPublicKey(ecdsa);
}

void GenerateKeys(int count)
{
    if (!File.Exists(PrivateKeyFile))
    {
        Console.WriteLine($"Error: {PrivateKeyFile} not found. Run 'init' first.");
        return;
    }

    using var ecdsa = ECDsa.Create();
    ecdsa.ImportFromPem(File.ReadAllText(PrivateKeyFile));

    Console.WriteLine($"Generating {count} Pro license key(s):\n");

    for (int i = 0; i < count; i++)
    {
        var key = CreateLicenseKey(ecdsa, tier: 1);
        Console.WriteLine(key);
    }
}

void VerifyKey(string key)
{
    if (!File.Exists(PrivateKeyFile))
    {
        Console.WriteLine($"Error: {PrivateKeyFile} not found. Run 'init' first.");
        return;
    }

    using var ecdsa = ECDsa.Create();
    ecdsa.ImportFromPem(File.ReadAllText(PrivateKeyFile));
    var parameters = ecdsa.ExportParameters(false);

    // Decode and verify
    if (!key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("INVALID: missing KS- prefix");
        return;
    }

    var base32Part = key[KeyPrefix.Length..].Replace("-", "");
    var bytes = Base32Decode(base32Part);
    if (bytes == null || bytes.Length != PayloadLength + 64)
    {
        Console.WriteLine($"INVALID: decoded to {bytes?.Length ?? 0} bytes, expected {PayloadLength + 64}");
        return;
    }

    var payload = bytes.AsSpan(0, PayloadLength);
    var signature = bytes.AsSpan(PayloadLength, 64);

    using var verifier = ECDsa.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = parameters.Q
    });

    if (verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256))
    {
        var version = payload[0];
        var tier = payload[1] == 1 ? "Pro" : "Free";
        var keyId = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(2, 8));
        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(10, 4));
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);

        Console.WriteLine("VALID");
        Console.WriteLine($"  Version:  {version}");
        Console.WriteLine($"  Tier:     {tier}");
        Console.WriteLine($"  Key ID:   {keyId}");
        Console.WriteLine($"  Issued:   {issuedAt:yyyy-MM-dd HH:mm:ss UTC}");
    }
    else
    {
        Console.WriteLine("INVALID: signature verification failed");
    }
}

string CreateLicenseKey(ECDsa ecdsa, byte tier)
{
    // Build 14-byte payload
    var payload = new byte[PayloadLength];
    payload[0] = 1; // version
    payload[1] = tier;

    // Random 8-byte key ID
    RandomNumberGenerator.Fill(payload.AsSpan(2, 8));

    // Current timestamp (uint32)
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(10, 4), now);

    // Sign
    var signature = ecdsa.SignData(payload, HashAlgorithmName.SHA256);

    // Concatenate payload + signature
    var combined = new byte[payload.Length + signature.Length];
    payload.CopyTo(combined, 0);
    signature.CopyTo(combined, payload.Length);

    // Base32 encode
    var base32 = Base32Encode(combined);

    // Format as KS-XXXXXXXX-XXXXXXXX-...
    return FormatKey(base32);
}

void PrintPublicKey(ECDsa ecdsa)
{
    var parameters = ecdsa.ExportParameters(false);
    Console.WriteLine("Copy these into LicenseService.cs:\n");
    Console.WriteLine($"private static readonly byte[] PublicKeyX = [{FormatBytes(parameters.Q.X!)}];");
    Console.WriteLine($"private static readonly byte[] PublicKeyY = [{FormatBytes(parameters.Q.Y!)}];");
}

string FormatBytes(byte[] bytes) =>
    string.Join(", ", bytes.Select(b => $"0x{b:X2}"));

string FormatKey(string base32)
{
    // Insert dashes every 8 chars
    var chunks = new List<string>();
    for (int i = 0; i < base32.Length; i += 8)
        chunks.Add(base32.Substring(i, Math.Min(8, base32.Length - i)));
    return KeyPrefix + string.Join("-", chunks);
}

// Base32 encode/decode (RFC 4648, no padding)
const string B32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

string Base32Encode(byte[] data)
{
    var chars = new char[(data.Length * 8 + 4) / 5];
    int bitBuffer = 0, bitsInBuffer = 0, index = 0;
    foreach (var b in data)
    {
        bitBuffer = (bitBuffer << 8) | b;
        bitsInBuffer += 8;
        while (bitsInBuffer >= 5)
        {
            bitsInBuffer -= 5;
            chars[index++] = B32Alphabet[(bitBuffer >> bitsInBuffer) & 0x1F];
        }
    }
    if (bitsInBuffer > 0)
        chars[index++] = B32Alphabet[(bitBuffer << (5 - bitsInBuffer)) & 0x1F];
    return new string(chars, 0, index);
}

byte[]? Base32Decode(string encoded)
{
    var bits = new List<byte>();
    int bitBuffer = 0, bitsInBuffer = 0;
    foreach (var c in encoded)
    {
        int val = c switch
        {
            >= 'A' and <= 'Z' => c - 'A',
            >= 'a' and <= 'z' => c - 'a',
            >= '2' and <= '7' => c - '2' + 26,
            _ => -1
        };
        if (val < 0) return null;
        bitBuffer = (bitBuffer << 5) | val;
        bitsInBuffer += 5;
        if (bitsInBuffer >= 8)
        {
            bitsInBuffer -= 8;
            bits.Add((byte)((bitBuffer >> bitsInBuffer) & 0xFF));
        }
    }
    return bits.ToArray();
}
