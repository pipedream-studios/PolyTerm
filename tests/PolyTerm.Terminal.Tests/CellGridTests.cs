using FluentAssertions;
using PolyTerm.Terminal;
using Xunit;

namespace PolyTerm.Terminal.Tests;

public sealed class CellGridTests
{
    [Fact]
    public void NewGrid_AllCellsEmpty()
    {
        var grid = new CellGrid(80, 24);
        grid[0, 0].IsBlank.Should().BeTrue();
        grid[79, 23].IsBlank.Should().BeTrue();
    }

    [Fact]
    public void WriteAndRead_RoundTrips()
    {
        var grid = new CellGrid(80, 24);
        grid[5, 3] = new Cell { Codepoint = 'A', Attribute = CellAttribute.Default };
        grid[5, 3].Codepoint.Should().Be('A');
    }

    [Fact]
    public void ScrollUp_MovesCells()
    {
        var grid = new CellGrid(10, 5);
        grid[0, 0] = new Cell { Codepoint = 'X', Attribute = CellAttribute.Default };
        grid.ScrollUp(1, 0, 4);
        grid[0, 0].Codepoint.Should().NotBe('X'); // row 0 now contains old row 1
    }

    [Fact]
    public void Resize_PreservesExistingContent()
    {
        var grid = new CellGrid(10, 5);
        grid[2, 1] = new Cell { Codepoint = 'Z', Attribute = CellAttribute.Default };
        grid.Resize(20, 10);
        grid[2, 1].Codepoint.Should().Be('Z');
    }

    [Fact]
    public void ClearRow_FillsWithBlank()
    {
        var grid = new CellGrid(10, 5);
        grid[3, 2] = new Cell { Codepoint = 'Q', Attribute = CellAttribute.Default };
        grid.ClearRow(2);
        grid[3, 2].IsBlank.Should().BeTrue();
    }
}
