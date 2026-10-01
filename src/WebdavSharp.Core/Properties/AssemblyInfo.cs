using System.Runtime.CompilerServices;

// 测试需要访问内部纯函数（路径根归一化等实现细节），不进公开 API。
[assembly: InternalsVisibleTo("WebdavSharp.Tests")]
