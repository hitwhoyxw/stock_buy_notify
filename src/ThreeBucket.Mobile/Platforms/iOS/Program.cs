using UIKit;

namespace ThreeBucket.UI;

/// <summary>iOS 程序入口（桌面版 Program.cs 已在共享时排除，此为移动端专用）。</summary>
public static class Program
{
    static void Main(string[] args)
    {
        UIApplication.CheckForIllegalCrossThreadCalls = false;
        _ = new AppDelegate();
    }
}
