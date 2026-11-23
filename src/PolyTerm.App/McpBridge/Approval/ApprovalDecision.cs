namespace PolyTerm.App.McpBridge.Approval;

/// <summary>
/// What <see cref="IApprovalGate.CheckAsync"/> decided and how (#228). <see cref="Policy"/> is the
/// policy that was actually applied: <c>"Allow"</c>, <c>"Deny"</c> or <c>"Prompt"</c> (an unrecognized
/// configured value is treated as, and reported as, <c>"Prompt"</c>). <see cref="Prompted"/> says
/// whether a person answered an approval dialog. A <c>"Prompt"</c> decision that wasn't prompted means
/// there was no main window to show the dialog in, so the action was denied (fail closed).
/// </summary>
internal sealed record ApprovalDecision(bool Allowed, string Policy, bool Prompted)
{
    /// <summary>Decided by an <c>"Allow"</c> or <c>"Deny"</c> policy, with no prompt.</summary>
    public static ApprovalDecision ByPolicy(string policy) => new(policy == "Allow", policy, Prompted: false);

    /// <summary>The user answered the approval dialog.</summary>
    public static ApprovalDecision ByUser(bool approved) => new(approved, "Prompt", Prompted: true);

    /// <summary>The policy is <c>"Prompt"</c> but there was no main window to show the dialog in.</summary>
    public static ApprovalDecision NoWindowToPrompt { get; } = new(false, "Prompt", Prompted: false);

    /// <summary>Who made the decision: <c>"user"</c> at a prompt, otherwise <c>"policy"</c>.</summary>
    public string DecidedBy => Prompted ? "user" : "policy";

    /// <summary>The <c>approval</c> field added to every gated tool's response.</summary>
    public object ToResponse() => new { policy = Policy, decided_by = DecidedBy };

    /// <summary>Why the action was denied, worded for the agent. Only meaningful when <see cref="Allowed"/> is false.</summary>
    public string DenialReason =>
        Prompted          ? "the user rejected it at the approval prompt"
        : Policy == "Deny" ? "the MCP approval policy is Deny"
        :                    "approval is required but there is no main window to show the prompt in, so it was denied (fail closed)";
}
