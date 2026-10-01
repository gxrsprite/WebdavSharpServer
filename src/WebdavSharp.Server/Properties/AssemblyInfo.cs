using System.Runtime.CompilerServices;

// 测试需要访问 DavMiddleware 的 internal 纯函数（Range 解析/合并、ETag 构造），
// 这些方法是实现细节，不进公开 API。
[assembly: InternalsVisibleTo("WebdavSharp.Tests")]
