using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using PolyTerm.App.Views;
using PolyTerm.Engine;

namespace PolyTerm.App;

/// <summary>
/// GUI implementation of <see cref="IHostKeyChangePrompt"/>: shows <see cref="HostKeyChangeDialog"/> on the UI
/// thread and awaits the user's choice. Fails closed (disconnect) when there is no main window to own the dialog.
/// </summary>
internal sealed class HostKeyChangePromptService : IHostKeyChangePrompt
{
    public async Task<HostKeyChangeChoice> AskAsync(HostKeyChangeInfo info, CancellationToken ct = default)
    {
        // InvokeAsync<Task<T>> flattens the inner task, so this awaits the dialog closing.
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            return owner is null
                ? Task.FromResult(HostKeyChangeChoice.Disconnect)
                : new HostKeyChangeDialog(info).ShowDialog<HostKeyChangeChoice>(owner);
        }).ConfigureAwait(false);
    }
}
