using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using PolyTerm.App.CommandPalette;
using PolyTerm.App.Import;
using PolyTerm.App.Macros;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using PolyTerm.Engine.Credentials;
using PolyTerm.Protocols.Ssh.X11;

namespace PolyTerm.App.Views;

/// <summary>
/// Main application window: hosts the connection tree, session tabs, and the menu/toolbar
/// actions (new connection, import, recording, quick connect, theme switching).
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Only shown when Bitwarden is the currently active saved provider - that's exactly the
        // condition under which a live BitwardenApiClient exists in this running instance's DI
        // container at all (AppCompositionRoot only registers it then; see VaultSettingsWindow's own
        // comment on the same gate). A URL typed into Settings but not yet saved/active wouldn't have
        // a running client to log into either, so checking the live registration is more accurate
        // than just checking whether a URL string happens to be set.
        BitwardenQuickLoginMenuItem.IsVisible = App.Services.GetService<BitwardenApiClient>() is not null;
    }

    /// <summary>
    /// Clamps the window to the working area of whichever screen it actually opened on — the fixed
    /// 1200x800 default (set in the .axaml) is larger than plenty of real displays (small laptops,
    /// secondary monitors, fixed-resolution RDP/VM sessions), and with no clamping the window would
    /// open partially or fully off-screen with no way to reach its own title bar to resize it. Runs in
    /// <see cref="OnOpened"/>, not the constructor, because <see cref="Window.Screens"/> only resolves
    /// the correct monitor once the window has actually been placed (<c>WindowStartupLocation</c> has
    /// taken effect).
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        // WorkingArea is in physical pixels (excludes the taskbar); Width/Height are DIPs — divide by
        // the screen's own scaling factor to convert, same as everywhere else in Avalonia sizing code.
        var maxWidth  = screen.WorkingArea.Width  / screen.Scaling;
        var maxHeight = screen.WorkingArea.Height / screen.Scaling;

        if (Width > maxWidth)  Width  = Math.Max(MinWidth, maxWidth);
        if (Height > maxHeight) Height = Math.Max(MinHeight, maxHeight);
    }

    /// <summary>Wires up tree edit/rename requests, and the close/reattach dialogs (issue #77 Phase 1), once the <see cref="MainViewModel"/> is bound.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
        {
            vm.ConnectionTree.EditRequested   += OnEditRequested;
            vm.ConnectionTree.RenameRequested += OnRenameRequested;

            vm.ConfirmCloseAsync = sessionName =>
                new CloseSessionDialog(sessionName).ShowDialog<CloseSessionChoice>(this);
            vm.PickDetachedSessionAsync = items =>
                new DetachedSessionsDialog(items).ShowDialog<DetachedSessionItem?>(this);
        }
    }

    /// <summary>
    /// Routes through the tapped row's own <see cref="ConnectionNodeViewModel.ConnectCommand"/> (rather
    /// than a tree-wide "connect selected" command) so double-click shares the same busy-tracking and
    /// error-handling path as the context menu's "Connect" item.
    /// </summary>
    private void ConnectionNode_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is StyledElement { DataContext: ConnectionNodeViewModel { CanConnect: true } node })
            _ = node.ConnectCommand.ExecuteAsync(null);
    }

    /// <summary>Ctrl+Shift+K opens quick-connect; Ctrl+Shift+P opens the command palette — both global shortcuts.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift)) return;

        if (e.Key == Key.K)
        {
            e.Handled = true;
            _ = ShowQuickConnectAsync();
        }
        else if (e.Key == Key.P)
        {
            e.Handled = true;
            _ = ShowCommandPaletteAsync();
        }
    }

    private void CommandPalette_Click(object? sender, RoutedEventArgs e) => _ = ShowCommandPaletteAsync();

    /// <summary>Opens the command palette (Ctrl+Shift+P) and runs whichever command the user confirms.</summary>
    private async Task ShowCommandPaletteAsync()
    {
        var dlgVm  = new CommandPaletteViewModel(BuildPaletteCommands());
        var dialog = new CommandPaletteWindow { DataContext = dlgVm };
        await dialog.ShowDialog(this);

        if (dlgVm is { Confirmed: true, SelectedCommand: not null })
            dlgVm.SelectedCommand.Execute();
    }

    /// <summary>
    /// Every action reachable from the command palette. Reuses the exact same click handlers wired to
    /// the menu/ribbon (calling them with this window as sender and an empty <see cref="RoutedEventArgs"/>
    /// — none of them actually inspect either), so the palette can never drift out of sync with what a
    /// menu item actually does; a menu item added later just needs one line here to also become
    /// palette-reachable. Tab-scoped entries are guarded the same way their originating handler already
    /// guards them (a null <c>ActiveTab</c> is a no-op, not an error).
    /// </summary>
    private List<PaletteCommand> BuildPaletteCommands()
    {
        var e = new RoutedEventArgs();
        List<PaletteCommand> commands =
        [
            new("New Connection…",       "Session",     null,          () => NewConnection_Click(this, e)),
            new("Quick Connect…",        "Session",     "Ctrl+Shift+K", () => QuickConnect_Click(this, e)),
            new("New Local Shell",       "Session",     null,          () => NewLocalShell_Click(this, e)),
            new("Toggle SFTP Panel",     "Session",     null,          () => ToggleSftp_Click(this, e)),
            new("Start Recording…",      "Session",     null,          () => StartRecording_Click(this, e)),
            new("Stop Recording",        "Session",     null,          () => StopRecording_Click(this, e)),
            new("Pause/Resume Recording","Session",     null,          () => ToggleRecordingPause_Click(this, e)),
            new("Manage Macros…",        "Session",     null,          () => Macros_Click(this, e)),
            new("New Folder…",           "Connections", null,          () => TreeNewFolder_Click(this, e)),
            new("Import mRemoteNG…",     "Import",      null,          () => ImportMRemoteNg_Click(this, e)),
            new("Import MobaXterm…",     "Import",      null,          () => ImportMobaXterm_Click(this, e)),
            new("Theme: Dark",           "Theme",       null,          () => ThemeDark_Click(this, e)),
            new("Theme: Light",          "Theme",       null,          () => ThemeLight_Click(this, e)),
            new("Theme: System",         "Theme",       null,          () => ThemeSystem_Click(this, e)),
            new("MCP Approval Policy: Prompt", "MCP",   null,          () => McpPolicyPrompt_Click(this, e)),
            new("MCP Approval Policy: Allow",  "MCP",   null,          () => McpPolicyAllow_Click(this, e)),
            new("MCP Approval Policy: Deny",   "MCP",   null,          () => McpPolicyDeny_Click(this, e)),
            new("MCP Rate Limit…",       "MCP",         null,          () => McpRateLimit_Click(this, e)),
            new("Toggle Connection Tree","View",        null,          () => ToggleConnectionTree_Click(this, e)),
        ];

        if (DataContext is MainViewModel vm)
        {
            commands.Add(new("Toggle Broadcast Mode", "Session", null, () => vm.ToggleBroadcastCommand.Execute(null)));
            commands.Add(new("Close Active Tab", "Session", null, () =>
            {
                if (vm.ActiveTab is { } tab) vm.CloseTabCommand.Execute(tab);
            }));
            if (vm.ActiveTab is { CanSendCtrlAltDelete: true } activeTab)
                commands.Add(new("Send Ctrl+Alt+Delete", "Session", null, () => _ = activeTab.SendCtrlAltDeleteAsync()));

            // "Go to connection" (VS Code's "Go to File", applied to saved connections) — reuses the
            // exact same ConnectCommand the tree's own double-click already calls (see
            // ConnectionNode_DoubleTapped), so this can never drift out of sync with what double-
            // clicking a tree row actually does. Folders and protocol-unavailable entries (e.g. RDP on
            // non-Windows) are excluded, matching CanConnect's existing gate on the tree UI itself.
            foreach (var node in FlattenConnectableNodes(vm.ConnectionTree.RootNodes))
                commands.Add(new(node.Name, "Connect", null, () => _ = node.ConnectCommand.ExecuteAsync(null)));
        }

        return commands;
    }

    private static IEnumerable<ConnectionNodeViewModel> FlattenConnectableNodes(IEnumerable<ConnectionNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.CanConnect)
                yield return node;
            foreach (var child in FlattenConnectableNodes(node.Children))
                yield return child;
        }
    }

    /// <summary>
    /// Prompts for an mRemoteNG confCons.xml file, imports it via <see cref="MRemoteNgImporter"/>
    /// (trying mRemoteNG's default master password first, then prompting for a real one if that
    /// fails to decrypt anything), writes any decrypted passwords to the OS keychain, persists the
    /// resulting profiles (folders before their children), reloads the tree, and reports a summary.
    /// </summary>
    private async void ImportMRemoteNg_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Import mRemoteNG Connections",
            AllowMultiple  = false,
            FileTypeFilter =
            [
                new FilePickerFileType("mRemoteNG XML") { Patterns = ["confCons.xml", "*.xml"] },
                new FilePickerFileType("All files")     { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0) return;

        var path       = files[0].Path.LocalPath;
        var store      = App.Services.GetRequiredService<IProfileStore>();
        var credWriter = App.Services.GetService<ICredentialWriter>();

        Import.ImportResult result;
        try { result = MRemoteNgImporter.Import(path); }
        catch (Exception ex)
        {
            vm.ConnectionError = $"Import failed: {ex.Message}";
            return;
        }

        if (result.PasswordDecryptFailedCount > 0)
        {
            var masterPassword = await new InputDialog(
                $"mRemoteNG Master Password ({result.PasswordDecryptFailedCount} password(s) need it — blank to skip)",
                "").ShowDialog<string?>(this);

            if (!string.IsNullOrEmpty(masterPassword))
            {
                try { result = MRemoteNgImporter.Import(path, masterPassword); }
                catch (Exception ex)
                {
                    vm.ConnectionError = $"Import failed: {ex.Message}";
                    return;
                }
            }
        }

        // Persist — parents are always before children in the list (depth-first walk).
        // Attach a decrypted password's credential before saving so it's written in one pass.
        var importedPasswordCount = 0;
        foreach (var profile in result.Profiles)
        {
            if (credWriter is not null && result.DecryptedPasswords.TryGetValue(profile.Id, out var password))
            {
                var username = profile.Settings.TryGetValue("username", out var u) ? u.GetString() ?? "" : "";
                profile.Credential = await credWriter.WriteAsync($"PolyTerm/{profile.Id}", username, password);
                importedPasswordCount++;
            }
            await store.SaveAsync(profile);
        }

        await vm.ConnectionTree.LoadCommand.ExecuteAsync(null);

        var conns   = result.Profiles.Count(p => !p.IsFolder);
        var folders = result.Profiles.Count(p => p.IsFolder);
        var msg     = $"Imported {conns} connection{(conns == 1 ? "" : "s")}";
        if (folders > 0) msg += $" in {folders} folder{(folders == 1 ? "" : "s")}";
        if (result.Skipped.Count > 0)
            msg += $" · {result.Skipped.Count} skipped (unsupported protocol)";
        if (importedPasswordCount > 0)
            msg += $" · {importedPasswordCount} password{(importedPasswordCount == 1 ? "" : "s")} imported";
        if (result.PasswordDecryptFailedCount > 0)
            msg += $" · {result.PasswordDecryptFailedCount} password{(result.PasswordDecryptFailedCount == 1 ? "" : "s")} could not be decrypted (wrong master password?)";
        vm.ImportResult = msg;
    }

    /// <summary>
    /// Prompts for a MobaXterm .mxtsessions file, imports it via <see cref="MobaXtermImporter"/>,
    /// persists the resulting profiles (folders before their children), reloads the tree, and
    /// reports a summary (imported/skipped counts; passwords are never imported).
    /// </summary>
    private async void ImportMobaXterm_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Import MobaXterm Sessions",
            AllowMultiple  = false,
            FileTypeFilter =
            [
                new FilePickerFileType("MobaXterm Sessions") { Patterns = ["*.mxtsessions"] },
                new FilePickerFileType("All files")           { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0) return;

        var store = App.Services.GetRequiredService<IProfileStore>();
        Import.ImportResult result;
        try
        {
            var path = files[0].Path.LocalPath;
            result = MobaXtermImporter.Import(path);
        }
        catch (Exception ex)
        {
            vm.ConnectionError = $"Import failed: {ex.Message}";
            return;
        }

        foreach (var profile in result.Profiles)
            await store.SaveAsync(profile);

        await vm.ConnectionTree.LoadCommand.ExecuteAsync(null);

        var conns   = result.Profiles.Count(p => !p.IsFolder);
        var folders = result.Profiles.Count(p => p.IsFolder);
        var msg     = $"Imported {conns} connection{(conns == 1 ? "" : "s")}";
        if (folders > 0) msg += $" in {folders} folder{(folders == 1 ? "" : "s")}";
        if (result.Skipped.Count > 0)
            msg += $" · {result.Skipped.Count} skipped (unsupported session type)";
        msg += " · Passwords were not imported";
        vm.ImportResult = msg;
    }

    /// <summary>
    /// Warns the user that recording captures all on-screen output (see docs/todos.md's Security
    /// section — command output, file contents, anything a script echoes, all land in the file
    /// verbatim), then prompts for a save path and starts recording the active tab (asciicast v2 or
    /// raw log, by extension).
    /// </summary>
    private async void StartRecording_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var tab = vm.ActiveTab;
        if (tab is null || tab.IsRecording) return;

        var confirmed = await new ConfirmDialog(
            "Start Recording?",
            "This records everything that appears in the terminal — command output, file contents, "
                + "and any secret a script prints to the screen — to a plain file on disk. Typed input "
                + "isn't recorded, but sharing the resulting file shares everything it captured. "
                + "You can pause recording before running something sensitive.",
            actionLabel: "Start Recording",
            destructive: false).ShowDialog<bool>(this);
        if (!confirmed) return;

        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title             = "Save Recording",
            SuggestedFileName = $"polyterm-{DateTime.Now:yyyyMMdd-HHmmss}",
            FileTypeChoices   =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("asciicast v2") { Patterns = ["*.cast"] },
                new Avalonia.Platform.Storage.FilePickerFileType("Raw terminal log") { Patterns = ["*.log"] },
            ],
        });

        if (file is null) return;

        var path      = file.Path.LocalPath;
        var asciicast = path.EndsWith(".cast", StringComparison.OrdinalIgnoreCase);
        tab.StartRecording(path, asciicast);
    }

    private void StopRecording_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.ActiveTab?.StopRecording();
    }

    /// <summary>Toggles pause/resume on the active tab's in-progress recording.</summary>
    private void ToggleRecordingPause_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var tab = vm.ActiveTab;
        if (tab is null || !tab.IsRecording) return;

        if (tab.IsRecordingPaused) tab.ResumeRecording();
        else tab.PauseRecording();
    }

    private void QuickConnect_Click(object? sender, RoutedEventArgs e) =>
        _ = ShowQuickConnectAsync();

    /// <summary>Shows the quick-connect dialog and, if confirmed, opens the parsed profile directly (no vault save).</summary>
    private async Task ShowQuickConnectAsync()
    {
        if (DataContext is not MainViewModel vm) return;
        var dlgVm  = new QuickConnectViewModel();
        var dialog = new QuickConnectWindow { DataContext = dlgVm };
        await dialog.ShowDialog(this);

        if (!dlgVm.Confirmed || dlgVm.ParsedProfile is null) return;

        var sessions = App.Services.GetRequiredService<ISessionManager>();
        vm.ConnectingStatus = $"Connecting to {dlgVm.ParsedProfile.Name}…";
        try
        {
            await sessions.OpenDirectAsync(dlgVm.ParsedProfile);
        }
        catch (Exception ex)
        {
            vm.ConnectionError = ex.Message;
        }
        finally
        {
            vm.ConnectingStatus = null;
        }
    }

    /// <summary>
    /// Inline quick-connect box above the connection tree: on Enter, parses the same
    /// <c>[user@]host[:port]</c>/<c>ssh://...</c> syntax as the Quick Connect dialog and opens
    /// the session directly (no vault save), clearing the box on success.
    /// </summary>
    private async void QuickConnectInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not MainViewModel vm || sender is not TextBox box) return;

        var profile = QuickConnectViewModel.ParseInput(box.Text?.Trim() ?? "");
        if (profile is null)
        {
            vm.ConnectionError = "Could not parse a host from that input.";
            return;
        }

        e.Handled = true;
        var sessions = App.Services.GetRequiredService<ISessionManager>();
        vm.ConnectingStatus = $"Connecting to {profile.Name}…";
        try
        {
            await sessions.OpenDirectAsync(profile);
            box.Text = "";
        }
        catch (Exception ex)
        {
            vm.ConnectionError = ex.Message;
        }
        finally
        {
            vm.ConnectingStatus = null;
        }
    }

    /// <summary>Prompts for a name and creates a new folder profile at the vault root.</summary>
    private async void TreeNewFolder_Click(object? sender, RoutedEventArgs e)
    {
        var name = await new InputDialog("New Folder", "").ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name)) return;

        var store = App.Services.GetRequiredService<IProfileStore>();
        await store.SaveAsync(new ConnectionProfile
        {
            Name       = name.Trim(),
            IsFolder   = true,
            ProtocolId = "",
        });

        if (DataContext is MainViewModel vm)
            await vm.ConnectionTree.LoadCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Opens the New Connection dialog for creating a fresh profile. Each File &gt; New Connection &gt;
    /// &lt;protocol&gt; submenu item stashes its protocol id in its own <see cref="Control.Tag"/> so this
    /// one handler can pre-select the right protocol — other call sites (the ribbon button, the
    /// connection tree's context menu, the command palette) leave <c>Tag</c> unset and get the dialog's
    /// own default (SSH) instead.
    /// </summary>
    private async void NewConnection_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var protocolId = (sender as Control)?.Tag as string;
        var store      = App.Services.GetRequiredService<IProfileStore>();
        var registry   = App.Services.GetRequiredService<IProtocolRegistry>();
        var credWriter = App.Services.GetService<ICredentialWriter>();
        var credProviders = App.Services.GetServices<ICredentialProvider>().ToList();
        var bundledX11Server = App.Services.GetRequiredService<IBundledX11ServerProvider>();
        var dialog = new NewConnectionWindow
        {
            DataContext = new NewConnectionViewModel(store, vm.ConnectionTree, registry, credWriter: credWriter,
                initialProtocolId: protocolId, credentialProviders: credProviders, bundledX11Server: bundledX11Server),
        };
        await dialog.ShowDialog(this);
    }

    /// <summary>Opens the New Connection dialog pre-populated with an existing profile for editing.</summary>
    private async void OnEditRequested(object? sender, ConnectionProfile profile)
    {
        if (DataContext is not MainViewModel vm) return;
        var store      = App.Services.GetRequiredService<IProfileStore>();

        // Folders have no host or protocol, so the connection dialog can never be saved for one.
        if (profile.IsFolder)
        {
            await new EditFolderWindow { DataContext = new EditFolderViewModel(store, profile, vm.ConnectionTree) }.ShowDialog(this);
            return;
        }

        var registry   = App.Services.GetRequiredService<IProtocolRegistry>();
        var credWriter = App.Services.GetService<ICredentialWriter>();
        var credProviders = App.Services.GetServices<ICredentialProvider>().ToList();
        var bundledX11Server = App.Services.GetRequiredService<IBundledX11ServerProvider>();
        var dialog = new NewConnectionWindow
        {
            DataContext = new NewConnectionViewModel(store, vm.ConnectionTree, registry, profile, credWriter,
                credentialProviders: credProviders, bundledX11Server: bundledX11Server),
        };
        await dialog.ShowDialog(this);
    }

    /// <summary>Opens the rename dialog for a profile requested from the connection tree.</summary>
    private async void OnRenameRequested(object? sender, ConnectionProfile profile)
    {
        if (DataContext is not MainViewModel vm) return;
        var store = App.Services.GetRequiredService<IProfileStore>();
        var dialog = new RenameWindow
        {
            DataContext = new RenameViewModel(store, profile, vm.ConnectionTree),
        };
        await dialog.ShowDialog(this);
    }

    /// <summary>Opens a new local shell session directly, without going through the vault.</summary>
    private async void NewLocalShell_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var sessions = App.Services.GetRequiredService<ISessionManager>();
        var profile  = new PolyTerm.Engine.ConnectionProfile
        {
            Name       = "Local Shell",
            ProtocolId = "local-shell",
        };
        vm.ConnectingStatus = "Starting local shell…";
        try
        {
            await sessions.OpenDirectAsync(profile);
        }
        catch (Exception ex)
        {
            vm.ConnectionError = ex.Message;
        }
        finally
        {
            vm.ConnectingStatus = null;
        }
    }

    private void ToggleSftp_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.ActiveTab?.ToggleSftpPanel();
    }

    private void ToggleConnectionTree_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.ConnectionTreeVisible = !vm.ConnectionTreeVisible;
    }

    private void SendCtrlAltDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { ActiveTab.CanSendCtrlAltDelete: true } vm)
            _ = vm.ActiveTab!.SendCtrlAltDeleteAsync();
    }

    /// <summary>
    /// Opens the non-modal Macros window, wiring its run/broadcast delegates to the active tab
    /// and the existing broadcast pipeline. Variables always expand against whichever tab is
    /// active at the moment a macro is run, not whichever was active when this window was opened.
    /// </summary>
    private void Macros_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var macrosVm = new MacrosViewModel(MacroStore.Load(), MacroStore.Save)
        {
            SendToActiveSessionAsync = async macro =>
            {
                if (vm.ActiveTab?.Terminal is not { } terminal) return false;
                var text = MacroExpander.Expand(macro.Body, vm.ActiveTab.Profile, vm.ActiveTab.Title);
                if (macro.AppendEnter) text += "\r";
                await terminal.SendInputAsync(Encoding.UTF8.GetBytes(text));
                return true;
            },
            BroadcastAsync = async macro =>
            {
                if (!vm.BroadcastEnabled) return false;
                var text = MacroExpander.Expand(macro.Body, vm.ActiveTab?.Profile, vm.ActiveTab?.Title);
                if (macro.AppendEnter) text += "\r";
                await vm.BroadcastCommand.ExecuteAsync(text);
                return true;
            },
        };

        new MacrosWindow { DataContext = macrosVm }.Show(this);
    }

    private void About_Click(object? sender, RoutedEventArgs e) => new AboutWindow().ShowDialog(this);

    private void TerminalFont_Click(object? sender, RoutedEventArgs e) => new TerminalFontWindow().ShowDialog(this);

    private void VaultSettings_Click(object? sender, RoutedEventArgs e) => new VaultSettingsWindow().ShowDialog(this);

    /// <summary>
    /// Opens <see cref="BitwardenLoginDialog"/> directly from the menu bar, without going through the
    /// full Vault Provider settings window first - only reachable when
    /// <see cref="BitwardenQuickLoginMenuItem"/> is visible (Bitwarden is the active provider), so
    /// <c>BitwardenApiClient</c> is guaranteed registered here.
    /// </summary>
    private async void BitwardenQuickLogin_Click(object? sender, RoutedEventArgs e)
    {
        var controller = App.Services.GetRequiredService<BitwardenApiClient>();
        var status = await controller.CheckStatusAsync().ConfigureAwait(true);
        await new BitwardenLoginDialog(controller, status).ShowDialog<bool>(this).ConfigureAwait(true);
    }

    private void ThemeDark_Click(object? sender, RoutedEventArgs e)   => SetAndSaveTheme("Dark");
    private void ThemeLight_Click(object? sender, RoutedEventArgs e)  => SetAndSaveTheme("Light");
    private void ThemeSystem_Click(object? sender, RoutedEventArgs e) => SetAndSaveTheme("Default");

    /// <summary>Applies the theme variant to the running app and persists it to <see cref="App.Settings"/>.</summary>
    private static void SetAndSaveTheme(string variant)
    {
        App.ApplyTheme(variant);
        App.Settings.ThemeVariant = variant;
        App.Settings.Save();
    }

    private void McpPolicyPrompt_Click(object? sender, RoutedEventArgs e) => SetAndSaveMcpPolicy("Prompt");
    private void McpPolicyAllow_Click(object? sender, RoutedEventArgs e)  => SetAndSaveMcpPolicy("Allow");
    private void McpPolicyDeny_Click(object? sender, RoutedEventArgs e)   => SetAndSaveMcpPolicy("Deny");

    /// <summary>
    /// Sets the global default MCP approval policy — takes effect immediately for future tool calls
    /// (<c>ApprovalGateService</c> reads <see cref="App.Settings"/> live, no restart needed) — and
    /// persists it. Individual connections can still override this via their own "MCP policy" field.
    /// </summary>
    private static void SetAndSaveMcpPolicy(string policy)
    {
        App.Settings.McpApprovalPolicy = policy;
        App.Settings.Save();
    }

    /// <summary>Prompts for a new per-connection MCP calls-per-minute limit and persists it. Blank/non-numeric input leaves the setting unchanged; 0 or less disables the limit.</summary>
    private async void McpRateLimit_Click(object? sender, RoutedEventArgs e)
    {
        var input = await new InputDialog(
            "MCP Rate Limit (calls/minute per connection, 0 = unlimited)",
            App.Settings.McpMaxCallsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture)).ShowDialog<string?>(this);

        if (input is null) return;
        if (!int.TryParse(input, out var limit)) return;

        App.Settings.McpMaxCallsPerMinute = limit;
        App.Settings.Save();
    }

    private static void Exit_Click(object? sender, RoutedEventArgs e) =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
}
