using ExtractXiso.DataStructures;

namespace ExtractXiso.Tests;

public class TypesTests
{
    [Fact]
    public void AvlResult_Values()
    {
        Assert.Equal(0, (int)AvlResult.NoErr);
        Assert.Equal(1, (int)AvlResult.AvlError);
        Assert.Equal(2, (int)AvlResult.AvlBalanced);
    }

    [Fact]
    public void AvlTraversalMethod_Values()
    {
        Assert.Equal(0, (int)AvlTraversalMethod.Prefix);
        Assert.Equal(1, (int)AvlTraversalMethod.Infix);
        Assert.Equal(2, (int)AvlTraversalMethod.Postfix);
    }

    [Fact]
    public void ExtractMode_Values()
    {
        Assert.Equal(0, (int)ExtractMode.GenerateAvl);
        Assert.Equal(1, (int)ExtractMode.Extract);
        Assert.Equal(2, (int)ExtractMode.List);
        Assert.Equal(3, (int)ExtractMode.Rewrite);
    }

    [Fact]
    public void ExtractError_Values()
    {
        Assert.Equal(-5001, (int)ExtractError.ErrEndOfSector);
        Assert.Equal(-5002, (int)ExtractError.ErrIsoRewritten);
        Assert.Equal(-5003, (int)ExtractError.ErrIsoNoFiles);
    }

    [Fact]
    public void CreateList_Defaults()
    {
        var list = new CreateList();
        Assert.Equal("", list.Path);
        Assert.Null(list.Name);
        Assert.Null(list.Next);
    }

    [Fact]
    public void CreateList_Linked()
    {
        var first = new CreateList { Path = "dir1", Name = "iso1" };
        var second = new CreateList { Path = "dir2", Name = "iso2", Next = first };

        Assert.Same(first, second.Next);
        Assert.Equal("dir1", second.Next.Path);
        Assert.Equal("iso1", second.Next.Name);
    }

    [Fact]
    public void FileTime_Default()
    {
        var ft = new FileTime();
        Assert.Equal(0u, ft.Low);
        Assert.Equal(0u, ft.High);
    }

    [Fact]
    public void FileTime_SetValues()
    {
        var ft = new FileTime { Low = 0xDEADBEEF, High = 0xCAFEBABE };
        Assert.Equal(0xDEADBEEFu, ft.Low);
        Assert.Equal(0xCAFEBABEu, ft.High);
    }

    [Fact]
    public void WdsafpContext_Defaults()
    {
        var ctx = new WdsafpContext();
        Assert.Equal(0, ctx.DirStart);
        Assert.Equal(0u, ctx.CurrentSector);
    }

    [Fact]
    public void WriteTreeContext_AutoProperty()
    {
        var ctx = new WriteTreeContext();
        Assert.Equal(0, ctx.FinalBytes);
        Assert.Null(ctx.Path);
        Assert.Null(ctx.Progress);
        Assert.Null(ctx.SourceStream);
    }

    [Fact]
    public void ProgressCallback_Invoke()
    {
        long receivedCurrent = -1;
        long receivedFinal = -1;

        ProgressCallback cb = (current, final) =>
        {
            receivedCurrent = current;
            receivedFinal = final;
        };

        cb(100, 1000);
        Assert.Equal(100, receivedCurrent);
        Assert.Equal(1000, receivedFinal);
    }

    [Fact]
    public void TraversalCallback_Invoke()
    {
        int receivedDepth = -1;
        var node = new AvlNode { Filename = "test" };

        TraversalCallback cb = (n, ctx, depth) =>
        {
            receivedDepth = depth;
            Assert.Same(node, n);
            Assert.Equal("ctx", ctx);
            return 42;
        };

        int result = cb(node, "ctx", 3);
        Assert.Equal(42, result);
        Assert.Equal(3, receivedDepth);
    }
}
