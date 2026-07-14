using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using AwesomeAssertions;
using PolyTerm.App.Views;

namespace PolyTerm.App.Tests;

/// <summary>Covers the fix for issue #64 (Help &gt; About PolyTerm previously had no handler wired at all).</summary>
public sealed class AboutWindowTests
{
    [AvaloniaFact]
    public void Constructor_PopulatesVersionLabelFromAssemblyInformationalVersion()
    {
        var about = new AboutWindow();

        var versionLabel = about.FindControl<TextBlock>("VersionLabel");
        versionLabel.Should().NotBeNull();
        versionLabel!.Text.Should().StartWith("Version ");
    }

    [AvaloniaFact]
    public async Task ClickingClose_ClosesTheWindow()
    {
        var owner = new Window();
        owner.Show();

        var about = new AboutWindow();
        var closeTcs = new TaskCompletionSource();
        about.Closed += (_, _) => closeTcs.TrySetResult();
        about.Show(owner);

        var closeButton = about.FindControl<Button>("CloseButton");
        closeButton.Should().NotBeNull();
        closeButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await closeTcs.Task;
    }
}
