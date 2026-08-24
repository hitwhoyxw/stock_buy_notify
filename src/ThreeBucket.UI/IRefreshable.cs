namespace ThreeBucket.UI;

/// <summary>选中标签页时触发数据刷新的契约（对应 PyQt5 的 showEvent）。</summary>
public interface IRefreshable
{
    void OnShown();
}
