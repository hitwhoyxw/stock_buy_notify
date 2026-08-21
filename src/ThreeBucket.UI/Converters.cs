using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ThreeBucket.UI;

public static class BucketColors
{
    public static readonly Dictionary<string, string> Map = new()
    {
        ["A"] = "#27ae60", ["B"] = "#2980b9", ["C"] = "#e74c3c", ["D"] = "#95a5a6",
    };
    public static string ColorOf(string b) => Map.TryGetValue((b ?? "").Trim().ToUpper(), out var c) ? c : "#333333";
}

public class BucketColorConverter : IValueConverter
{
    public object? Convert(object? v, Type? t, object? p, CultureInfo? c) =>
        new SolidColorBrush(Color.Parse(BucketColors.ColorOf(v as string)));
    public object? ConvertBack(object? v, Type? t, object? p, CultureInfo? c) => null;
}

public class PnlColorConverter : IValueConverter
{
    public object? Convert(object? v, Type? t, object? p, CultureInfo? c)
    {
        if (v is double d)
            return new SolidColorBrush(Color.Parse(d > 0 ? "#e74c3c" : d < 0 ? "#27ae60" : "#555555"));
        return new SolidColorBrush(Colors.Gray);
    }
    public object? ConvertBack(object? v, Type? t, object? p, CultureInfo? c) => null;
}

/// <summary>
/// 动态字典行取值（候选池等列名来自 CSV 表头的表格）：
/// 绑定行对象本身，ConverterParameter 传列名。
/// 不走索引器路径语法（[key] / ['key'] 在 Avalonia 版本间行为不一致，曾致整列空白）。
/// </summary>
public class DictValueConverter : IValueConverter
{
    public object? Convert(object? v, Type? t, object? p, CultureInfo? c) =>
        v is Dictionary<string, string> d && p is string key ? d.GetValueOrDefault(key, "") : "";
    public object? ConvertBack(object? v, Type? t, object? p, CultureInfo? c) => null;
}
