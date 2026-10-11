using System;
using System.Collections.Generic;
using XamppUpdate.Services.Uat;
using Xunit;

namespace XamppUpdate.Tests;

public class UatIniRewriterTests
{
    private const string Old = @"C:\xampp\mysql";
    private const string New = @"C:\xampp\mysql_copy_upgrade";

    [Theory]
    [InlineData(@"basedir=C:\xampp\mysql", @"basedir=C:\xampp\mysql_copy_upgrade")]
    [InlineData(@"datadir=C:/xampp/mysql/data", @"datadir=C:/xampp/mysql_copy_upgrade/data")]
    [InlineData(@"datadir=""C:\\xampp\\mysql\\data""", @"datadir=""C:\\xampp\\mysql_copy_upgrade\\data""")]
    [InlineData(@"log-error=c:\XAMPP\MYSQL\data\err.log", @"log-error=C:\xampp\mysql_copy_upgrade\data\err.log")]
    public void RewriteRoot_ReplacesAllForms(string input, string expected)
    {
        Assert.Equal(expected, UatIniRewriter.RewriteRoot(input, Old, New));
    }

    [Theory]
    [InlineData(@"backup=C:\xampp\mysql_old\data")]
    [InlineData(@"x=C:\xampp\mysqlbackup")]
    [InlineData(@"x=C:\xampp\mysql.bak\data")]
    public void RewriteRoot_DoesNotTouchSiblingNames(string input)
    {
        Assert.Equal(input, UatIniRewriter.RewriteRoot(input, Old, New));
    }

    [Fact]
    public void RewriteRoot_IsIdempotent()
    {
        string once = UatIniRewriter.RewriteRoot(@"basedir=C:\xampp\mysql", Old, New);
        Assert.Equal(once, UatIniRewriter.RewriteRoot(once, Old, New));
    }

    [Fact]
    public void RewriteRoot_HandlesRootsWithSpaces()
    {
        string result = UatIniRewriter.RewriteRoot(@"basedir=C:\xampp test\mysql", @"C:\xampp test\mysql", @"C:\xampp test\mysql copy");
        Assert.Equal(@"basedir=C:\xampp test\mysql copy", result);
    }

    [Fact]
    public void FindRemainingRoot_IgnoresCommentsAndSiblings()
    {
        string ini = "# basedir=C:\\xampp\\mysql\r\n; old C:\\xampp\\mysql\r\nx=C:\\xampp\\mysql_old\r\ndatadir=C:\\xampp\\mysql\\data\r\n";
        var found = UatIniRewriter.FindRemainingRoot(ini, Old);
        Assert.Single(found);
        Assert.Contains("datadir", found[0]);
    }

    [Fact]
    public void SetOption_ReplacesExistingActiveKeyInSection()
    {
        string ini = "[client]\nport=3306\n[mysqld]\nport=3306\nsocket=x\n";
        string result = UatIniRewriter.SetOption(ini, "mysqld", "port", "13306");
        Assert.Equal("[client]\nport=3306\n[mysqld]\nport=13306\nsocket=x\n", result);
    }

    [Fact]
    public void SetOption_AddsKeyWhenMissingAndCreatesSectionWhenAbsent()
    {
        string withSection = UatIniRewriter.SetOption("[mysqld]\nport=3306\n", "mysqld", "bind-address", "127.0.0.1");
        Assert.Equal("127.0.0.1", UatIniRewriter.GetOption(withSection, "mysqld", "bind-address"));

        string noClient = UatIniRewriter.SetOption("[mysqld]\nport=3306\n", "client", "port", "13306");
        Assert.Equal("13306", UatIniRewriter.GetOption(noClient, "client", "port"));
        Assert.Equal("3306", UatIniRewriter.GetOption(noClient, "mysqld", "port"));
    }

    [Fact]
    public void SetOption_PreservesCrLfAndBom()
    {
        string ini = "\uFEFF[mysqld]\r\nport=3306\r\n";
        string result = UatIniRewriter.SetOption(ini, "mysqld", "port", "13306");
        Assert.StartsWith("\uFEFF[mysqld]", result);
        Assert.Contains("\r\n", result);
        Assert.DoesNotContain("\r\n\r\n", result);
        Assert.Equal("13306", UatIniRewriter.GetOption(result, "mysqld", "port"));
    }

    [Fact]
    public void SetOption_IgnoresCommentedKeys()
    {
        string ini = "[mysqld]\n# port=3306\n";
        string result = UatIniRewriter.SetOption(ini, "mysqld", "port", "13306");
        Assert.Equal("13306", UatIniRewriter.GetOption(result, "mysqld", "port"));
        Assert.Contains("# port=3306", result);
    }

    [Fact]
    public void CommentOutOptions_DisablesNamedPipeAndSocket()
    {
        string ini = "[mysqld]\nsocket=\"C:/x/mysql.sock\"\nenable-named-pipe\nport=1\n";
        string result = UatIniRewriter.CommentOutOptions(ini, "mysqld", "socket", "enable-named-pipe");
        Assert.Null(UatIniRewriter.GetOption(result, "mysqld", "socket"));
        Assert.Equal("1", UatIniRewriter.GetOption(result, "mysqld", "port"));
    }

    [Theory]
    [InlineData("!include C:/x.cnf", true)]
    [InlineData("  !includedir C:/conf.d", true)]
    [InlineData("# !include C:/x.cnf", false)]
    [InlineData("[mysqld]\nport=3306", false)]
    public void ContainsInclude_DetectsActiveIncludes(string ini, bool expected)
    {
        Assert.Equal(expected, UatIniRewriter.ContainsInclude(ini));
    }

    [Fact]
    public void FindExternalPaths_FlagsWritablePathsOutsideAllowedRoot()
    {
        string ini = "[mysqld]\ndatadir=C:/xampp/mysql_copy_upgrade/data\ntmpdir=D:/tmp\nlog-error=C:/xampp/mysql/data/err.log\nport=1\n";
        var external = UatIniRewriter.FindExternalPaths(ini, @"C:\xampp\mysql_copy_upgrade");
        Assert.Equal(2, external.Count);
        Assert.Contains(external, e => e.Contains("tmpdir"));
        Assert.Contains(external, e => e.Contains("log-error"));
    }

    [Fact]
    public void RedactSecrets_MasksPasswords()
    {
        string result = UatIniRewriter.RedactSecrets("[client]\npassword = s3cr3t!\nuser=root\n");
        Assert.DoesNotContain("s3cr3t", result);
        Assert.Contains("user=root", result);
    }
}
