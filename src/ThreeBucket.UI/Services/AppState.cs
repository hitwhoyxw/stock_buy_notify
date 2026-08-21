using System.IO;
using System.Reflection;
using ThreeBucket.Core.Data;
using ThreeBucket.Core.Models;
using ThreeBucket.Core.Services;

namespace ThreeBucket.UI.Services;

/// <summary>应用级共享状态：解析项目根与数据目录，提供数据访问与行情服务。</summary>
public class AppState
{
    public string ProjectRoot { get; }
    public string DataDir { get; }
    public DataStore Store { get; }
    public QuoteService Quotes { get; }
    public AppConfig Config { get; set; }

    public AppState()
    {
        ProjectRoot = DetectProjectRoot();
        DataDir = Path.Combine(ProjectRoot, "data");
        Store = new DataStore(DataDir);
        Quotes = new QuoteService();
        Config = Store.LoadConfig();
        if (string.IsNullOrEmpty(Config.DataDir)) Config.DataDir = DataDir;
        if (string.IsNullOrEmpty(Config.ProjectRoot)) Config.ProjectRoot = ProjectRoot;
    }

    /// <summary>从 exe 目录逐级向上查找含 scripts/ 的目录作为项目根。</summary>
    private static string DetectProjectRoot()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                  ?? AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "scripts")))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        // 兜底：当前目录或 exe 目录
        if (Directory.Exists(Path.Combine(System.Environment.CurrentDirectory, "scripts")))
            return System.Environment.CurrentDirectory;
        return dir;
    }
}
