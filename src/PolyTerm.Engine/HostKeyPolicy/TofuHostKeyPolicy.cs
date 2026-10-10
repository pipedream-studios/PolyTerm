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

    private readonly IHostKeyChangePrompt? _prompt;

    /// <param name="prompt">Interactive front end's changed-key prompt; null for headless hosts, which reject changed keys.</param>
    public TofuHostKeyPolicy(IHostKeyStore store, ILogger<TofuHostKeyPolicy> logger, IHostKeyChangePrompt? prompt = null)
    {
        _store = store;
        _logger = logger;
        _prompt = prompt;
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

        // Key has changed — never auto-accept. The front end owns the user-visible dialog; with none
        // registered (CLI/MCP) we return Prompt, which the SSH session treats as a rejection.
        _logger.LogWarning(
            "Host key CHANGED for {Host}:{Port}! Old={Old}, New={New}",
            key.Host, key.Port, known.FingerprintSha256, key.FingerprintSha256);

        if (_prompt is null)
            return HostKeyDecision.Prompt;

        var choice = await _prompt.AskAsync(
            new HostKeyChangeInfo(key.Host, key.Port, key.Algorithm, known.FingerprintSha256, known.FirstSeenAt, key.FingerprintSha256),
            ct).ConfigureAwait(false);

        switch (choice)
        {
            case HostKeyChangeChoice.TrustPermanently:
                await _store.TrustAsync(key, ct).ConfigureAwait(false); // upsert replaces the old fingerprint
                _logger.LogWarning("User permanently trusted changed host key for {Host}:{Port}", key.Host, key.Port);
                return HostKeyDecision.Trust;
            case HostKeyChangeChoice.TrustOnce:
                _logger.LogWarning("User trusted changed host key once for {Host}:{Port}", key.Host, key.Port);
                return HostKeyDecision.Trust;
            default:
                return HostKeyDecision.Reject;
        }
    }
}
