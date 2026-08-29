using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Monkeysphere.Web.Security;

public sealed class AdministratorCredential
{
    private readonly PasswordHasher<AdministratorIdentity> _hasher;
    private readonly AdministratorIdentity _identity;
    private readonly string _passwordHash;

    private AdministratorCredential(string username, string password)
    {
        Username = username;
        _identity = new AdministratorIdentity(username);
        _hasher = new PasswordHasher<AdministratorIdentity>(Options.Create(new PasswordHasherOptions
        {
            IterationCount = 210_000,
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        }));
        _passwordHash = _hasher.HashPassword(_identity, password);
    }

    public string Username { get; }

    public bool Verify(string username, string password)
    {
        if (!string.Equals(Username, username?.Trim(), StringComparison.Ordinal))
        {
            _ = _hasher.VerifyHashedPassword(_identity, _passwordHash, password ?? string.Empty);
            return false;
        }

        PasswordVerificationResult result = _hasher.VerifyHashedPassword(_identity, _passwordHash, password ?? string.Empty);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }

    public static AdministratorCredential Load(IConfiguration configuration)
    {
        string username = (configuration["MONKEYSPHERE_ADMIN_USERNAME"] ?? "admin").Trim();
        if (username.Length is < 1 or > 100)
        {
            throw new InvalidOperationException("The administrator username must contain between 1 and 100 characters.");
        }

        string? directPassword = configuration["MONKEYSPHERE_ADMIN_PASSWORD"];
        string? passwordFile = configuration["MONKEYSPHERE_ADMIN_PASSWORD_FILE"];
        if (!string.IsNullOrWhiteSpace(directPassword) && !string.IsNullOrWhiteSpace(passwordFile))
        {
            throw new InvalidOperationException("Configure either MONKEYSPHERE_ADMIN_PASSWORD or MONKEYSPHERE_ADMIN_PASSWORD_FILE, not both.");
        }

        string password = !string.IsNullOrWhiteSpace(passwordFile)
            ? ReadPasswordFile(passwordFile)
            : directPassword ?? "admin";
        ValidatePassword(password);
        return new AdministratorCredential(username, password);
    }

    private static string ReadPasswordFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        FileInfo file = new(fullPath);
        if (!file.Exists)
        {
            throw new InvalidOperationException("The configured administrator password file does not exist.");
        }

        if (file.Length is < 1 or > 4096)
        {
            throw new InvalidOperationException("The administrator password file has an invalid size.");
        }

        return File.ReadAllText(fullPath).TrimEnd('\r', '\n');
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length > 1024)
        {
            throw new InvalidOperationException("The administrator password must contain between 1 and 1024 non-blank characters.");
        }
    }

    private sealed record AdministratorIdentity(string Username);
}
