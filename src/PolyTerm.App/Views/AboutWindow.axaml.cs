using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PolyTerm.App.Views;

/// <summary>Simple, static About dialog: app name, version (from the assembly's informational version), and a repo link.</summary>
public sealed partial class AboutWindow : Window
{
    private const string RepoUrl = "https://github.com/chris3081/PolyTerm";

    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionLabel.Text = version is null ? string.Empty : $"Version {version}";
    }

    private void RepoLink_Click(object? sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
