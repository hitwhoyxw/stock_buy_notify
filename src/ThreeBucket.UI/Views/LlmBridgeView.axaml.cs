using System.Diagnostics;
using Avalonia.Controls;
using ThreeBucket.UI.Dialogs;
using ThreeBucket.UI.Services;

namespace ThreeBucket.UI.Views;

public partial class LlmBridgeView : UserControl, IRefreshable
{
    private readonly AppState _app;
    private readonly Action<string> _status;

    private class SkillDef
    {
        public string Key = "";
        public string Label = "";
        public string Input = "";
        public string Output = "";
        public string Script = "";      // 生成输入对应的脚本（相对项目根）
        public string Args = "";
    }

    private static readonly SkillDef[] Skills =
    {
        new() { Key = "T4C", Label = "T4 财报文本扫描", Input = "skill_input_T4C.md", Output = "skill_output_T4C.md", Script = "scripts/t4_ingest.py", Args = "--prepare" },
        new() { Key = "T5",  Label = "T5 季度归因",     Input = "skill_input_T5.md",  Output = "skill_output_T5.md",  Script = "scripts/t5_prepare.py",  Args = "" },
        new() { Key = "T6A", Label = "T6 A桶·红利逆向", Input = "skill_input_T6_A.md", Output = "skill_output_T6_A.md", Script = "scripts/t6_candidate_pool.py", Args = "--bucket A --top 200" },
        new() { Key = "T6B", Label = "T6 B桶·成长",     Input = "skill_input_T6_B.md", Output = "skill_output_T6_B.md", Script = "scripts/t6_candidate_pool.py", Args = "--bucket B --top 200" },
        new() { Key = "T6C", Label = "T6 C桶·热点周期", Input = "skill_input_T6_C.md", Output = "skill_output_T6_C.md", Script = "scripts/t6_candidate_pool.py", Args = "--bucket C --top 200" },
    };

    private bool _running;

    /// <summary>仅供 XAML 编译器/设计器使用；运行时请用带参构造。</summary>
    public LlmBridgeView() : this(new AppState(), _ => { }) { }

    public LlmBridgeView(AppState app, Action<string> status)
    {
        InitializeComponent();
        _app = app; _status = status;

        SkillCombo.ItemsSource = Skills.Select(s => $"{s.Key} — {s.Label}").ToList();
        SkillCombo.SelectedIndex = 0;

        GenBtn.Click += async (_, _) => await GenInput();
        CopyBtn.Click += (_, _) => CopyInput();
        ReloadBtn.Click += (_, _) => Load();
        SaveBtn.Click += (_, _) => SaveOutput();
        ImportBtn.Click += (_, _) => ImportFile();
        SkillCombo.SelectionChanged += (_, _) => Load();
    }

    public void OnShown() => Load();

    private SkillDef Cur => Skills[SkillCombo.SelectedIndex < 0 ? 0 : SkillCombo.SelectedIndex];

    private void Load()
    {
        var s = Cur;
        InputText.Text = _app.Store.LoadText(s.Input);
        var mi = _app.Store.FileMtime(s.Input);
        InputInfo.Text = mi.Length > 0 ? $"{_app.Store.LoadText(s.Input).Length} | {mi}" : "未生成";

        OutputText.Text = _app.Store.LoadText(s.Output);
        var mo = _app.Store.FileMtime(s.Output);
        OutputInfo.Text = mo.Length > 0 ? $"{_app.Store.LoadText(s.Output).Length} | {mo}" : "未保存";
    }

    private async Task GenInput()
    {
        if (_running) { _status("生成中，请等待…"); return; }
        // 移动端（iOS/Android）无本地 Python 环境，Process.Start 也不受支持
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            _status("移动端不支持运行本地脚本，请在桌面端生成后查看");
            return;
        }
        var s = Cur;
        var py = string.IsNullOrWhiteSpace(_app.Config.PythonExe) ? "python" : _app.Config.PythonExe;
        var script = Path.Combine(_app.ProjectRoot, s.Script);
        if (!File.Exists(script))
        {
            _status($"脚本不存在: {s.Script}");
            return;
        }
        _running = true;
        GenBtn.IsEnabled = false;
        GenBtn.Content = "⏳ 运行中…";
        _status($"运行 {s.Script} …");

        var psi = new ProcessStartInfo
        {
            FileName = py,
            Arguments = $"\"{script}\" {s.Args}".Trim(),
            WorkingDirectory = _app.ProjectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var proc = Process.Start(psi)!;
            var err = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode == 0)
            {
                Load();
                _status($"✓ {s.Key} skill_input 已生成，请点击右侧「复制」");
            }
            else
            {
                _status($"✗ {s.Key} 生成失败 (exit {proc.ExitCode}) {err[..Math.Min(120, err.Length)]}");
            }
        }
        catch (Exception ex)
        {
            _status($"✗ 执行异常: {ex.Message}");
        }
        finally
        {
            _running = false;
            GenBtn.IsEnabled = true;
            GenBtn.Content = "⚡ 生成输入（运行脚本）";
        }
    }

    private void CopyInput()
    {
        var text = InputText.Text;
        if (string.IsNullOrEmpty(text)) { _status("skill_input 为空，请先生成"); return; }
        if (this.VisualRoot is TopLevel tl) _ = tl.Clipboard?.SetTextAsync(text);
        _status("已复制到剪贴板，请粘贴到 Qoder 对话框获取 LLM 分析");
    }

    private void SaveOutput()
    {
        var s = Cur;
        var text = OutputText.Text;
        if (string.IsNullOrWhiteSpace(text)) { _status("输出内容为空"); return; }
        if (_app.Store.SaveText(s.Output, text))
        {
            var mo = _app.Store.FileMtime(s.Output);
            OutputInfo.Text = $"已保存 | {mo}";
            _status($"已保存到 data/{s.Output}");
        }
        else
        {
            _status("保存失败，请检查文件权限");
        }
    }

    private async void ImportFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "导入 skill_output",
            Filters = { new FileDialogFilter { Name = "Markdown", Extensions = { "md" } }, new FileDialogFilter { Name = "All", Extensions = { "*" } } },
        };
        var owner = this.VisualRoot as Window;
        var files = owner != null ? await dlg.ShowAsync(owner) : null;
        if (files is { Length: > 0 })
        {
            try { OutputText.Text = File.ReadAllText(files[0], new System.Text.UTF8Encoding(false)); _status($"已从 {Path.GetFileName(files[0])} 导入"); }
            catch (Exception ex) { _status($"导入失败: {ex.Message}"); }
        }
    }
}
