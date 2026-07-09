namespace PolyTerm.App.Macros;

/// <summary>A saved, app-global named text snippet, sendable to the active session or broadcast to all.</summary>
public sealed class MacroDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>Template text, may contain <c>{{host}}</c>/<c>{{port}}</c>/<c>{{name}}</c>/<c>{{username}}</c>/<c>{{sessionTitle}}</c> tokens expanded by <see cref="MacroExpander"/>.</summary>
    public string Body { get; set; } = "";

    /// <summary>Whether to send a trailing Enter ("\r") after variable expansion, so the macro executes as a command line.</summary>
    public bool AppendEnter { get; set; } = true;
}
