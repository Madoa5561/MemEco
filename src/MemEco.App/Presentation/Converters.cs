using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace MemEco.App.Presentation;

/// <summary>enum と文字列（ConverterParameter）の一致で true を返す。セグメント選択に使用。</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
        {
            return false;
        }
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter != null && targetType.IsEnum)
        {
            return Enum.Parse(targetType, parameter.ToString()!, true);
        }
        return Binding.DoNothing;
    }
}

/// <summary>true / false で Visibility を切り替える（bool の反転付き）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>UI 表示用の書式。</summary>
public static class Format
{
    private const double Kb = 1024d;
    private const double MegaByte = 1024d * 1024d;
    private const double GigaByte = 1024d * 1024d * 1024d;

    public static string Bytes(ulong bytes)
    {
        if (bytes >= (ulong)GigaByte)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", bytes / GigaByte);
        }
        if (bytes >= (ulong)MegaByte)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0} MB", bytes / MegaByte);
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:0} KB", Math.Max(0, bytes / Kb));
    }

    /// <summary>解放量。0 は「0 MB」ではなく「0 B」で正直に。</summary>
    public static string Freed(ulong bytes)
    {
        if (bytes == 0)
        {
            return "0 MB";
        }
        return Bytes(bytes);
    }

    public static string Gb(ulong bytes)
        => string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", bytes / GigaByte);

    public static string Percent(double ratio, int digits = 0)
        => string.Format(CultureInfo.InvariantCulture, "{0:0." + new string('0', digits) + "}%", ratio * 100);

    public static string Clock(DateTime utc)
        => utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}
