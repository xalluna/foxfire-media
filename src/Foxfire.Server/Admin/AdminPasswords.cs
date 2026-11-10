using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace Foxfire.Server.Admin;

/// <summary>Password hashing (Identity's PBKDF2) and the admin password/username rules.</summary>
public sealed partial class AdminPasswords
{
    public const int MinLength = 12;
    public const int MaxLength = 256;

    private static readonly string[] Weak =
    [
        "password", "changeme", "change-me", "replace-me", "replaceme", "placeholder", "admin123", "letmein", "qwerty", "123456",
    ];

    private readonly PasswordHasher<AdminUser> _hasher = new();

    // Verified against when the username does not exist, so response time does not reveal valid usernames.
    private readonly string _dummyHash;

    public AdminPasswords() => _dummyHash = _hasher.HashPassword(null!, Guid.NewGuid().ToString());

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$")]
    private static partial Regex UsernamePattern();

    public static string? ValidateUsername(string? username) =>
        username is not null && UsernamePattern().IsMatch(username)
            ? null
            : "Username must be 3-64 characters: letters, digits, '.', '_' or '-'.";

    public static string? ValidatePassword(string? password)
    {
        if (password is null || password.Length < MinLength)
        {
            return $"Password must be at least {MinLength} characters.";
        }

        if (password.Length > MaxLength)
        {
            return $"Password must be at most {MaxLength} characters.";
        }

        if (password.Distinct().Count() < 6)
        {
            return "Password is too repetitive.";
        }

        var lower = password.ToLowerInvariant();
        return Weak.Any(lower.Contains) ? "Password is too common or looks like a placeholder." : null;
    }

    public string Hash(AdminUser user, string password) => _hasher.HashPassword(user, password);

    /// <returns>Whether the password matches; <paramref name="rehash"/> is set when the stored hash should be upgraded.</returns>
    public bool Verify(AdminUser? user, string password, out bool rehash)
    {
        rehash = false;
        if (user is null)
        {
            _hasher.VerifyHashedPassword(null!, _dummyHash, password);
            return false;
        }

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        rehash = result == PasswordVerificationResult.SuccessRehashNeeded;
        return result != PasswordVerificationResult.Failed;
    }
}
