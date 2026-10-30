namespace Uniform.Matrix.Client.Avalonia.Core.Views;

using System.Globalization;
using global::Avalonia.Data.Converters;
using global::Avalonia.Media;
using global::Avalonia.Media.Immutable;

/// <summary>Turn a user or room ID into one of a few colours, the same one every time, for names and avatars.</summary>
public sealed class SenderColorConverter : IValueConverter
{
    // Readable on light and dark backgrounds
    private static readonly IBrush[] Palette = [.. new[] { "#3B82F6", "#10B981", "#F59E0B", "#EF4444", "#8B5CF6", "#EC4899", "#14B8A6", "#F97316" }.Select(c => new ImmutableSolidColorBrush(Color.Parse(c)))];

    /// <summary>Return the colour of an ID.</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Stable hash of the text
        if (value is not string text || text.Length == 0) return Palette[0];
        uint hash = 2166136261;
        foreach (char character in text) hash = (hash ^ character) * 16777619;
        // Return brush
        return Palette[hash % Palette.Length];
    }

    /// <summary>Not supported.</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
