using Microsoft.Extensions.Logging;

namespace PolyTerm.Engine.HostKeyPolicy;

/// <summary>
/// Trust On First Use: auto-trusts unknown hosts, blocks on changed keys.
/// Changed keys require explicit user action (not auto-accepted).
/// </summary>
internal sealed class TofuHostKeyPolicy : IHostKeyPolicy
{
    private readonly IHostKeyStore _store;
    private readonly ILogger<TofuHostKeyPolicy> _logger;

    public TofuHostKeyPolicy(IHostKeyStore store, ILogger<TofuHostKeyPolicy> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Trusts (and records) hosts seen for the first time, silently re-trusts hosts whose key
    /// hasn't changed, and returns <see cref="HostKeyDecision.Prompt"/> — never an automatic
    /// trust — when a previously-seen host's key fingerprint has changed.
    /// </summary>
    public async Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default)
    {
        var known = await _store.FindAsync(key.Host, key.Port, key.Algorithm, ct).ConfigureAwait(false);

        if (known is null)
        {
            // TOFU: first time, record and allow
            await _store.TrustAsync(key, ct).ConfigureAwait(false);
            _logger.LogInformation("TOFU: trusted new host key {Alg}/{Fp} for {Host}:{Port}",
                key.Algorithm, key.FingerprintSha256, key.Host, key.Port);
            return HostKeyDecision.Trust;
        }

        if (known.FingerprintSha256 == key.FingerprintSha256)
        {
            await _store.TrustAsync(key, ct).ConfigureAwait(false); // update last_seen
            return HostKeyDecision.Trust;
        }

        // Key has changed — always surface this to the caller as a prompt requirement.
        // The App or CLI owns the user-visible dialog; we never auto-accept changed keys.
        _logger.LogWarning(
            "Host key CHANGED for {Host}:{Port}! Old={Old}, New={New}",
            key.Host, key.Port, known.FingerprintSha256, key.FingerprintSha256);

        return HostKeyDecision.Prompt;
    }
}
