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
