using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Weesite.Web.Visitors;

/// <summary>A challenge for the browser to solve, as <c>/visitor/challenge</c> sends it.</summary>
/// <param name="Algorithm">The key derivation: always <c>PBKDF2/SHA-256</c>.</param>
/// <param name="Challenge">The key to find, in lowercase hex.</param>
/// <param name="Salt">PBKDF2's salt: the purpose, the expiry and random hex, dot-separated.</param>
/// <param name="Cost">PBKDF2's iterations.</param>
/// <param name="MaxNumber">The largest number to try.</param>
/// <param name="Signature">What the browser sends back with the number it found.</param>
internal sealed record ProofChallenge(string Algorithm, string Challenge, string Salt, int Cost, int MaxNumber, string Signature);

/// <summary>
/// The invisible proof of work (ADR 0011), in ALTCHA's deterministic scheme: the server picks a
/// number, and sends the PBKDF2-SHA-256 key derived from that number (as text) with the salt; the
/// browser tries numbers from 0 until the key matches. PBKDF2 rather than a single SHA-256 (T-0188):
/// Web Crypto runs its iterations in native code, so a browser solves nearly as fast as a native
/// solver, where each <c>digest</c> call's fixed cost made it about 30 times slower. The signature is Data Protection over the hash and the salt, so any replica can check an
/// answer without storing the challenge; the salt carries the purpose and the expiry. An answer is
/// <c>number:signature</c>, and works once.
/// </summary>
/// <param name="protection">Data Protection, whose keys every replica shares.</param>
/// <param name="store">Where used challenges are kept.</param>
/// <param name="time">The clock.</param>
/// <param name="options">How hard a challenge is.</param>
internal sealed class ProofOfWork(IDataProtectionProvider protection, IVisitorStore store, TimeProvider time, VisitorOptions options)
{
    /// <summary>The purposes a challenge can be for.</summary>
    public static readonly IReadOnlySet<string> Purposes = new HashSet<string>(StringComparer.Ordinal) { "contact", "comment", "reaction" };

    /// <summary>How long a challenge can be answered.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The key derivation, in ALTCHA's name for it.</summary>
    public const string Algorithm = "PBKDF2/SHA-256";

    /// <summary>
    /// PBKDF2's iterations: about 1 ms a number in a browser, 0.7 ms in native code. Fewer would
    /// leave more of each try to Web Crypto's fixed cost a call, which a native solver doesn't pay.
    /// </summary>
    public const int Iterations = 5_000;

    private readonly IDataProtector _protector = protection.CreateProtector("Weesite.Visitors.ProofOfWork");

    /// <summary>Makes a challenge.</summary>
    /// <param name="purpose">What its answer may be used for: one of <see cref="Purposes"/>.</param>
    /// <returns>The challenge.</returns>
    public ProofChallenge Create(string purpose)
    {
        if (!Purposes.Contains(purpose))
        {
            throw new ArgumentException($"Unknown purpose '{purpose}'.", nameof(purpose));
        }

        var expires = time.GetUtcNow() + Lifetime;
        var salt = string.Create(CultureInfo.InvariantCulture, $"{purpose}.{expires.ToUnixTimeSeconds()}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12))}");
        var number = RandomNumberGenerator.GetInt32(0, options.ProofMaxNumber + 1);
        var challenge = HashOf(salt, number);
        return new ProofChallenge(Algorithm, challenge, salt, Iterations, options.ProofMaxNumber, _protector.Protect($"{challenge}\n{salt}"));
    }

    /// <summary>Checks an answer, and marks its challenge used.</summary>
    /// <param name="purpose">What it's used for.</param>
    /// <param name="answer">The answer: <c>number:signature</c>.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <returns>True when the answer is right, in time, for this purpose and not used before.</returns>
    public async Task<bool> VerifyAsync(string purpose, string? answer, CancellationToken cancellationToken)
    {
        if (Read(answer) is not var (number, signature) || Unprotect(signature) is not { } payload)
        {
            return false;
        }

        var lines = payload.Split('\n');
        var parts = lines.Length == 2 ? lines[1].Split('.') : [];
        if (parts.Length != 3 || !string.Equals(parts[0], purpose, StringComparison.Ordinal)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var challenge = lines[0];
        if (expires < time.GetUtcNow()
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(HashOf(lines[1], number)), Encoding.ASCII.GetBytes(challenge)))
        {
            return false;
        }

        return await store.TryUseChallengeAsync(challenge, expires, cancellationToken);
    }

    /// <summary>The key a number derives with a salt, as the browser computes it.</summary>
    /// <param name="salt">The salt.</param>
    /// <param name="number">The number: PBKDF2's password, as decimal text.</param>
    /// <returns>32 bytes in lowercase hex.</returns>
    public static string HashOf(string salt, int number) =>
        Convert.ToHexStringLower(Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(number.ToString(CultureInfo.InvariantCulture)),
            Encoding.UTF8.GetBytes(salt),
            Iterations,
            HashAlgorithmName.SHA256,
            32));

    /// <summary>Solves a challenge, as the browser does: for tests.</summary>
    /// <param name="challenge">The challenge.</param>
    /// <returns>The answer, or null when no number up to the maximum matches.</returns>
    public static string? Solve(ProofChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        for (var number = 0; number <= challenge.MaxNumber; number++)
        {
            if (HashOf(challenge.Salt, number) == challenge.Challenge)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{number}:{challenge.Signature}");
            }
        }

        return null;
    }

    private static (int Number, string Signature)? Read(string? answer)
    {
        var colon = answer?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        return colon > 0 && int.TryParse(answer.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? (number, answer![(colon + 1)..])
            : null;
    }

    private string? Unprotect(string signature)
    {
        try
        {
            return _protector.Unprotect(signature);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
