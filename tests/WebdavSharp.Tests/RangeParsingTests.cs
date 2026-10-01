using System.Net.Http.Headers;
using WebdavSharp.Server.WebDav;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>
/// Range 解析与合并单测（RFC 9110 §14）。多区间/去重合并是客户端（下载器）实际会发的形态，
/// 这里锁定语义：不可满足区间被丢弃、重叠相邻合并、无 Range 或全不可满足 → null/空。
/// </summary>
public class RangeParsingTests
{
    private static List<(long From, long To)>? Parse(string header, long total) =>
        DavMiddleware.ParseRanges(header, total);

    [Theory]
    [InlineData("bytes=0-9", 0, 9)]
    [InlineData("bytes=10-", 10, 99)]
    [InlineData("bytes=-10", 90, 99)]
    [InlineData("bytes=0-999", 0, 99)]     // 越界收敛到末尾
    [InlineData("bytes=99-99", 99, 99)]
    public void Parse_SingleRange(string header, long from, long to)
    {
        var r = Parse(header, 100);
        Assert.NotNull(r);
        Assert.Single(r!);
        Assert.Equal((from, to), r[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-9")]        // 非 bytes 单位 → 整个头不可识别
    public void Parse_NotABytesRange_ReturnsNull(string? header)
    {
        Assert.Null(Parse(header!, 100));
    }

    [Theory]
    [InlineData("bytes=abc-def")]    // 格式坏的 spec：丢弃后无可满足区间
    [InlineData("bytes=0")]          // 无连字符的 spec：同样丢弃
    public void Parse_MalformedSpec_ReturnsEmptyList(string header)
    {
        // 头本身是合法 Range 语法，但没有任何区间可满足 → 空列表（调用方回退 200 全量）
        var r = Parse(header, 100);
        Assert.NotNull(r);
        Assert.Empty(r!);
    }

    [Fact]
    public void Parse_MultiRange_PreservesAll()
    {
        var r = Parse("bytes=0-9,50-59,90-99", 100);
        Assert.NotNull(r);
        Assert.Equal(3, r!.Count);
        Assert.Equal((0L, 9L), r[0]);
        Assert.Equal((50L, 59L), r[1]);
        Assert.Equal((90L, 99L), r[2]);
    }

    [Fact]
    public void Parse_MultiRange_DropsUnsatisfiable_KeepsValid()
    {
        // 第 2 段起点越界：丢弃；第 3 段格式坏：丢弃
        var r = Parse("bytes=0-9,100-200,junk,-5", 100);
        Assert.NotNull(r);
        Assert.Equal(2, r!.Count);
        Assert.Equal((0L, 9L), r[0]);
        Assert.Equal((95L, 99L), r[1]); // "-5" 末尾 5 字节
    }

    [Fact]
    public void Parse_MultiRange_AllUnsatisfiable_ReturnsEmpty()
    {
        var r = Parse("bytes=100-200,300-400", 100);
        Assert.NotNull(r);
        Assert.Empty(r!);
    }

    [Fact]
    public void Parse_Overlapping_Ranges_AreMerged()
    {
        var r = Parse("bytes=0-20,10-30,100-110", 200);
        Assert.NotNull(r);
        Assert.Equal(2, r!.Count);
        Assert.Equal((0L, 30L), r[0]);   // 重叠合并
        Assert.Equal((100L, 110L), r[1]);
    }

    [Fact]
    public void Parse_Adjacent_Ranges_AreMerged()
    {
        var r = Parse("bytes=0-9,10-19", 100);
        Assert.NotNull(r);
        Assert.Single(r!);
        Assert.Equal((0L, 19L), r[0]);   // 相邻（last.To+1 == next.From）也合并
    }

    [Fact]
    public void Parse_ZeroLengthFile_ReturnsNull()
    {
        Assert.Null(Parse("bytes=0-9", 0));
    }

    [Fact]
    public void Merge_UnsortedInput_SortsThenMerges()
    {
        var merged = DavMiddleware.MergeRanges([(50L, 59L), (0L, 9L), (5L, 15L)]);
        Assert.Equal(2, merged.Count);
        Assert.Equal((0L, 15L), merged[0]);
        Assert.Equal((50L, 59L), merged[1]);
    }

    [Fact]
    public void ETag_IsStableWeakFormat()
    {
        var info = new FileInfo(Path.GetTempFileName());
        try
        {
            var etag = DavMiddleware.BuildETag(info);
            Assert.StartsWith("W/\"", etag);
            Assert.EndsWith("\"", etag);
            Assert.Equal(etag, DavMiddleware.BuildETag(info)); // 稳定
        }
        finally
        {
            info.Delete();
        }
    }

    [Fact]
    public void ContentRangeAndRangeHeaders_FormatAsExpected()
    {
        Assert.Equal("bytes 0-9/100", new ContentRangeHeaderValue(0, 9, 100).ToString());
        Assert.Equal("bytes=0-9", new RangeHeaderValue(0, 9).ToString());
    }

    [Fact]
    public void MultipartBody_ByteCount_MatchesBuildPartHeaderContract()
    {
        // 这个契约曾经踩坑：段头 Content-Type 若误用 multipart 自身类型，
        // Content-Length 预算与实际写出不符 → Kestrel 抛 mismatch。
        // 这里用与 ServeFileAsync 相同的拼装规则复算，锁定逐字节一致性。
        const string boundary = "DAVTESTBOUNDARY";
        const string fileType = "application/octet-stream";
        const long total = 1000;
        var ranges = new List<(long From, long To)> { (0, 9), (50, 59) };

        var expected = new System.Text.StringBuilder();
        foreach (var (from, to) in ranges)
        {
            expected.Append($"\r\n--{boundary}\r\n")
                    .Append($"Content-Type: {fileType}\r\n")
                    .Append($"Content-Range: bytes {from}-{to}/{total}\r\n\r\n");
            expected.Append('x', (int)(to - from + 1));
            expected.Append("\r\n");
        }
        expected.Append($"--{boundary}--\r\n");

        var actualBytes = System.Text.Encoding.ASCII.GetByteCount(expected.ToString());
        var budget = DavMiddleware.ComputeMultipartLengthForTest(ranges, total, boundary, fileType);
        Assert.Equal(actualBytes, budget);
    }
}
