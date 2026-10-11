using System.Security.Cryptography;
using SmartBuy.Domain.Entities;

namespace SmartBuy.Infrastructure.Persistence;

/// <summary>
/// Per-request (or per-job) identity. Starts with no organization, which makes every tenant-owned query return
/// nothing and every tenant-owned write fail until authentication fills it in.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private int _elevation;

    public Guid? OrganizationId { get; set; }
    public Guid? UserId { get; set; }
    public OrgRole? Role { get; set; }
    public bool IsPlatformAdmin { get; set; }
    public bool BypassTenantFilter => _elevation > 0;

    /// <summary>
    /// Temporarily lifts tenant isolation for code that legitimately spans organizations (sign-in, platform
    /// administration, system jobs). Always use in a <c>using</c> block and keep the block as small as possible.
    /// </summary>
    public IDisposable Elevate()
    {
        _elevation++;
        return new Scope(this);
    }

    private sealed class Scope(TenantContext owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._elevation--;
        }
    }
}

/// <summary>PBKDF2-HMAC-SHA256 with a per-password salt. Format: <c>pbkdf2-sha256$iterations$salt$hash</c>.</summary>
public static class PasswordHasher
{
    /// <summary>Work factor for new hashes. Lowered only by the automated test suite to keep it fast.</summary>
    public static int Iterations { get; set; } = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>True when the hash was made with fewer iterations than the current policy and should be upgraded at next login.</summary>
    public static bool NeedsRehash(string stored) =>
        !(stored.Split('$') is [_, var iterations, _, _] && int.TryParse(iterations, out var n) && n >= Iterations);

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
