using AwesomeAssertions;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Folders were edited through the connection dialog, which needs a host and so could never be saved.
/// The folder dialog offers Root plus every folder except the edited one and its own subtree.
/// </summary>
public sealed class EditFolderViewModelTests
{
    private static ConnectionProfile Folder(string name, Guid? parent = null) =>
        new() { Name = name, IsFolder = true, ProtocolId = "", ParentId = parent };

    [Fact]
    public void ParentChoices_ExcludeTheFolderAndItsDescendants()
    {
        var a = Folder("A");
        var b = Folder("B", a.Id);
        var c = Folder("C", b.Id);
        var d = Folder("D");
        var conn = new ConnectionProfile { Name = "srv", ProtocolId = "ssh", Host = "h", ParentId = d.Id };

        var choices = EditFolderViewModel.BuildParentChoices([a, b, c, d, conn], a);

        choices.Select(f => f.DisplayName).Should().Equal("— Root —", "D");
    }

    [Fact]
    public void ParentChoices_ForALeafFolder_OfferEveryOtherFolderWithItsPath()
    {
        var a = Folder("A");
        var b = Folder("B", a.Id);
        var c = Folder("C");

        var choices = EditFolderViewModel.BuildParentChoices([a, b, c], c);

        choices.Select(f => f.DisplayName).Should().Equal("— Root —", "A", "A / B");
        choices[0].Id.Should().BeNull();
    }
}
