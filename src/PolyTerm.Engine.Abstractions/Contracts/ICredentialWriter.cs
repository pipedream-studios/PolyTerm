namespace PolyTerm.Engine;

public interface ICredentialWriter
{
    /// <summary>
    /// Stores a password credential in the OS keychain and returns a reference to it.
    /// The key must be unique per profile (e.g. "PolyTerm/{profileId}").
    /// </summary>
    Task<CredentialReference> WriteAsync(string key, string username, string password, CancellationToken ct = default);
}
