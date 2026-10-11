using XamppUpdate.Services.Uat;
using Xunit;

namespace XamppUpdate.Tests;

public class PathSafetyTests
{
    [Theory]
    [InlineData(@"C:\xampp\mysql\bin\mysqld.exe", @"C:\xampp\mysql", true)]
    [InlineData(@"C:\xampp\mysql", @"C:\xampp\mysql", true)]
    [InlineData(@"C:\xampp\mysql\", @"C:\xampp\mysql", true)]
    [InlineData(@"c:\XAMPP\MySQL\bin\mysqld.exe", @"C:\xampp\mysql\", true)]
    [InlineData(@"C:\xampp\mysql_copy_upgrade\bin\mysqld.exe", @"C:\xampp\mysql", false)]
    [InlineData(@"C:\xampp\mysql_old\bin\mysqld.exe", @"C:\xampp\mysql", false)]
    [InlineData(@"C:\xampp\mysql\..\mysql_old\x.exe", @"C:\xampp\mysql", false)]
    [InlineData(@"C:\xampp test\mysql copy\bin\mysqld.exe", @"C:\xampp test\mysql copy", true)]
    public void IsSameOrUnder_RespectsSegmentBoundaries(string path, string root, bool expected)
    {
        Assert.Equal(expected, PathSafety.IsSameOrUnder(path, root));
    }

    [Fact]
    public void Overlaps_IsSymmetric()
    {
        Assert.True(PathSafety.Overlaps(@"C:\xampp\mysql", @"C:\xampp\mysql\uat"));
        Assert.True(PathSafety.Overlaps(@"C:\xampp\mysql\uat", @"C:\xampp\mysql"));
        Assert.False(PathSafety.Overlaps(@"C:\xampp\mysql", @"C:\xampp\mysql_copy_upgrade"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsProcessUnderRoot_UnreadablePathIsNeverOwned(string? image)
    {
        Assert.False(PathSafety.IsProcessUnderRoot(image, @"C:\xampp\mysql"));
    }
}
