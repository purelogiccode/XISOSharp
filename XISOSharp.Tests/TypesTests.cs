using XISOSharp.DataStructures;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for enum value correctness and data structure types
/// (<see cref="AvlResult"/>, <see cref="AvlTraversalMethod"/>,
/// <see cref="ExtractMode"/>, <see cref="ExtractError"/>,
/// <see cref="CreateList"/>, and callback delegates).
/// </summary>
public class TypesTests
{
    /// <summary>
    /// Verifies the integer values of <see cref="AvlResult"/> enum members.
    /// </summary>
    [Fact]
    public void AvlResult_Values()
    {
        Assert.Equal(0, (int)AvlResult.NoErr);
        Assert.Equal(1, (int)AvlResult.AvlError);
        Assert.Equal(2, (int)AvlResult.AvlBalanced);
    }

    /// <summary>
    /// Verifies the integer values of <see cref="AvlTraversalMethod"/> enum members.
    /// </summary>
    [Fact]
    public void AvlTraversalMethod_Values()
    {
        Assert.Equal(0, (int)AvlTraversalMethod.Prefix);
        Assert.Equal(1, (int)AvlTraversalMethod.Infix);
        Assert.Equal(2, (int)AvlTraversalMethod.Postfix);
    }

    /// <summary>
    /// Verifies the integer values of <see cref="ExtractMode"/> enum members.
    /// </summary>
    [Fact]
    public void ExtractMode_Values()
    {
        Assert.Equal(0, (int)ExtractMode.GenerateAvl);
        Assert.Equal(1, (int)ExtractMode.Extract);
        Assert.Equal(2, (int)ExtractMode.List);
        Assert.Equal(3, (int)ExtractMode.Rewrite);
    }

    /// <summary>
    /// Verifies the integer values of <see cref="ExtractError"/> enum members.
    /// </summary>
    [Fact]
    public void ExtractError_Values()
    {
        Assert.Equal(-5001, (int)ExtractError.ErrEndOfSector);
        Assert.Equal(-5002, (int)ExtractError.ErrIsoRewritten);
        Assert.Equal(-5003, (int)ExtractError.ErrIsoNoFiles);
    }

    /// <summary>
    /// Verifies that a newly constructed <see cref="CreateList"/> has expected default field values.
    /// </summary>
    [Fact]
    public void CreateList_Defaults()
    {
        var list = new CreateList();
        Assert.Equal("", list.Path);
        Assert.Null(list.Name);
        Assert.Null(list.Next);
    }

    /// <summary>
    /// Verifies that <see cref="CreateList"/> instances can be linked via the <c>Next</c> property,
    /// forming a singly-linked list.
    /// </summary>
    [Fact]
    public void CreateList_Linked()
    {
        var first = new CreateList { Path = "dir1", Name = "iso1" };
        var second = new CreateList { Path = "dir2", Name = "iso2", Next = first };

        Assert.Same(first, second.Next);
        Assert.Equal("dir1", second.Next.Path);
        Assert.Equal("iso1", second.Next.Name);
    }

    /// <summary>
    /// Verifies that the <c>ProgressCallback</c> delegate can be invoked with
    /// current and final long values, and that the callback receives them correctly.
    /// </summary>
    [Fact]
    public void ProgressCallback_Invoke()
    {
        long receivedCurrent;
        long receivedFinal;

        Cb(100, 1000);
        Assert.Equal(100, receivedCurrent);
        Assert.Equal(1000, receivedFinal);
        return;

        void Cb(long current, long final)
        {
            receivedCurrent = current;
            receivedFinal = final;
        }
    }

    /// <summary>
    /// Verifies that the <c>TraversalCallback</c> delegate can be invoked with
    /// an <see cref="AvlNode"/>, a context object, and a depth value,
    /// and that the callback receives and can return values correctly.
    /// </summary>
    [Fact]
    public void TraversalCallback_Invoke()
    {
        int receivedDepth;
        var node = new AvlNode { Filename = "test" };

        var result = Cb(node, "ctx", 3);
        Assert.Equal(42, result);
        Assert.Equal(3, receivedDepth);
        return;

        int Cb(AvlNode n, object? ctx, int depth)
        {
            receivedDepth = depth;
            Assert.Same(node, n);
            Assert.Equal("ctx", ctx);
            return 42;
        }
    }
}
