
namespace SMBLibrary.Authentication.NTLM
{
    public enum AVPairKey : ushort
    {
        EOL = 0x0000,
        NbComputerName = 0x0001, // Unicode
        NbDomainName = 0x0002, // Unicode
        DnsComputerName = 0x0003, // Unicode
        DnsDomainName = 0x0004, // Unicode
        DnsTreeName = 0x0005, // Unicode
        Flags = 0x0006, // UInt32
        // WebdavSharp patch: 上游笔误把 Timestamp 也写成 0x0006（与 Flags 冲突）。
        // MS-NLMP 2.2.2.1 规定 MsvAvTimestamp = 0x0007；不改的话完整 TargetInfo
        // 会在线上写出两个 id-6，严格客户端解析错乱。
        Timestamp = 0x0007, // Filetime
        SingleHost = 0x0008, // platform-specific BLOB
        TargetName = 0x0009, // Unicode
        ChannelBindings = 0x000A, // MD5 Hash
    }
}
