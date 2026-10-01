using System.Collections.Generic;
using System.Text;
using SMBLibrary.Server.SMB1;
using Xunit;

namespace WebdavSharp.Tests;

/// <summary>
/// RAP NetShareEnum 编解码单测（CX 根目录枚举的线格式）。
/// 请求向量取自真实抓包 <c>artifacts/cx-smb.pcap</c>（pkt 45，JCIFS 发出的 level 1 请求），
/// 响应布局按 [MS-RAP] 2.5.6.3.2 与 impacket 服务端实现交叉核对。
/// </summary>
public class RapHelperTests
{
    // CX 抓包原文：opcode=0, "WrLeh", "B13BWz", level=1, bufsize=0xFDFF
    private static readonly byte[] CxLevel1Request =
    [
        0x00, 0x00,
        0x57, 0x72, 0x4C, 0x65, 0x68, 0x00,
        0x42, 0x31, 0x33, 0x42, 0x57, 0x7A, 0x00,
        0x01, 0x00, 0xFF, 0xFD,
    ];

    private static List<RapHelper.ShareEntry> Shares(params (string Name, ushort Type, string Remark)[] items)
    {
        var list = new List<RapHelper.ShareEntry>();
        foreach (var item in items)
            list.Add(new RapHelper.ShareEntry(item.Name, item.Type, item.Remark));
        return list;
    }

    [Fact]
    public void CxRequest_Level1_ReturnsTwoDiskShares()
    {
        RapHelper.GetNetShareEnumResponse(CxLevel1Request, 4096,
            Shares(("public", 0, ""), ("NAS", 0, "")), out var pars, out var data);

        // RAPOutParams: status=0, convert=0, returned=2, available=2
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x02, 0x00 }, pars);

        // 2 个 20 字节条目 + 2 个空 remark（各 1 个 NUL）
        Assert.Equal(42, data.Length);

        // 条目 1：public，type=0，remark 偏移 40
        Assert.Equal("public", Encoding.ASCII.GetString(data, 0, 6));
        Assert.Equal(0, data[13]); // pad
        Assert.Equal(0, data[14] | (data[15] << 8)); // type
        int off1 = data[16] | (data[17] << 8);
        Assert.Equal(40, off1);

        // 条目 2：NAS，remark 偏移 41
        Assert.Equal("NAS", Encoding.ASCII.GetString(data, 20, 3));
        int off2 = data[36] | (data[37] << 8);
        Assert.Equal(41, off2);

        // remark 区：两个 NUL
        Assert.Equal(0, data[40]);
        Assert.Equal(0, data[41]);
    }

    [Fact]
    public void Level1_RemarkOffsetsPointAtCorrectStrings()
    {
        RapHelper.GetNetShareEnumResponse(CxLevel1Request, 4096,
            Shares(("public", 0, "open share"), ("NAS", 0, "media")), out var pars, out var data);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x02, 0x00 }, pars);

        int off1 = data[16] | (data[17] << 8);
        int off2 = data[36] | (data[37] << 8);
        Assert.Equal("open share", ReadZ(data, off1));
        Assert.Equal("media", ReadZ(data, off2));
        // 第二个 remark 紧跟第一个之后
        Assert.Equal(off1 + "open share".Length + 1, off2);
    }

    [Fact]
    public void Level0_Returns13ByteNamesWithoutRemarks()
    {
        var req = new byte[]
        {
            0x00, 0x00,
            0x57, 0x72, 0x4C, 0x65, 0x68, 0x00,
            0x42, 0x31, 0x33, 0x00,
            0x00, 0x00, 0x00, 0x10,
        };
        RapHelper.GetNetShareEnumResponse(req, 4096,
            Shares(("public", 0, "ignored"), ("NAS", 0, "ignored")), out var pars, out var data);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x02, 0x00 }, pars);
        Assert.Equal(26, data.Length);
        Assert.Equal("public", Encoding.ASCII.GetString(data, 0, 6));
        Assert.Equal("NAS", Encoding.ASCII.GetString(data, 13, 3));
    }

    [Fact]
    public void BadParamDesc_ReturnsInvalidParameter()
    {
        var req = (byte[])CxLevel1Request.Clone();
        req[2] = (byte)'X'; // 破坏 "WrLeh"
        RapHelper.GetNetShareEnumResponse(req, 4096, Shares(("public", 0, "")), out var pars, out var data);

        Assert.Equal(RapHelper.Win32InvalidParameter, pars[0] | (pars[1] << 8));
        Assert.Equal(0, pars[4] | (pars[5] << 8)); // returned=0
        Assert.Empty(data);
    }

    [Fact]
    public void Level2_ReturnsInvalidLevel()
    {
        var req = (byte[])CxLevel1Request.Clone();
        req[15] = 0x02; // level=2（本实现只做 0/1）
        RapHelper.GetNetShareEnumResponse(req, 4096, Shares(("public", 0, "")), out var pars, out var data);

        Assert.Equal(RapHelper.Win32InvalidLevel, pars[0] | (pars[1] << 8));
        Assert.Empty(data);
    }

    [Fact]
    public void LongName_IsExcludedPerSpec()
    {
        // 13 字节字段含 NUL：超过 12 字符的名字按 [MS-RAP] 不得出现在枚举中
        RapHelper.GetNetShareEnumResponse(CxLevel1Request, 4096,
            Shares(("public", 0, ""), ("averylongsharename", 0, "")), out var pars, out var data);

        Assert.Equal(1, pars[4] | (pars[5] << 8)); // returned=1
        Assert.Equal(1, pars[6] | (pars[7] << 8)); // available=1（长名已剔除）
        Assert.Equal(21, data.Length); // 20 + 1 NUL
    }

    [Fact]
    public void SmallBuffer_ReturnsMoreData()
    {
        // 缓冲只够 1 个条目 + remark：应截断并报 ERROR_MORE_DATA
        RapHelper.GetNetShareEnumResponse(CxLevel1Request, 21,
            Shares(("public", 0, ""), ("NAS", 0, "")), out var pars, out var data);

        Assert.Equal(RapHelper.Win32MoreData, pars[0] | (pars[1] << 8));
        Assert.Equal(1, pars[4] | (pars[5] << 8));
        Assert.Equal(2, pars[6] | (pars[7] << 8));
        Assert.Equal(21, data.Length);
    }

    [Fact]
    public void EmptyRequest_ReturnsInvalidParameter()
    {
        RapHelper.GetNetShareEnumResponse([], 4096, Shares(("public", 0, "")), out var pars, out var data);
        Assert.Equal(RapHelper.Win32InvalidParameter, pars[0] | (pars[1] << 8));
        Assert.Empty(data);
    }

    private static string ReadZ(byte[] data, int offset)
    {
        int end = offset;
        while (end < data.Length && data[end] != 0)
            end++;
        return Encoding.ASCII.GetString(data, offset, end - offset);
    }
}
