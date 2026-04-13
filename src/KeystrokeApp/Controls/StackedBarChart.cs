using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KeystrokeApp.Controls;

/// <summary>
/// Stacked vertical bar chart rendered on a Canvas.
/// Each bar has up to three segments (accepted/native/dismissed).
/// Designed for the dark Keystroke Settings theme.
/// </summary>
public class StackedBarChart : Canvas
{
    public record BarData(
        string Label,
        int Accepted,
        int Native,
        int Dismissed,
        bool IsToday = false);

    private readonly List<BarData> _bars = new();
    private readonly ToolTip _tooltip = new()
    {
        Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
        Foreground = new SolidColorBrush(Color.FromRgb(240, 246, 252)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61)),
        FontSize = 11,
        Padding = new Thickness(8, 4, 8, 4)
    };

    private static readonly Color AcceptColor = Color.FromRgb(63, 185, 80);     // green
    private static readonly Color NativeColor = Color.FromRgb(46, 169, 143);     // teal
    private static readonly Color DismissColor = Color.FromRgb(72, 79, 88);      // muted
    private static readonly Color TodayGlow = Color.FromArgb(40, 47, 129, 247);  // blue glow

    private const double BarGap = 2;
    private const double TopPadding = 4;
    private const double BottomPadding = 4;

    public StackedBarChart()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
    }

    public void SetBars(List<BarData> bars)
    {
        _bars.Clear();
        _bars.AddRange(bars);
    }

    public void Render()
    {
        Children.Clear();
        if (_bars.Count == 0) return;

        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        if (w <= 0 || h <= 0) return;

        int maxTotal = _bars.Max(b => b.Accepted + b.Native + b.Dismissed);
        if (maxTotal <= 0) maxTotal = 1;

        double barWidth = (w - (_bars.Count - 1) * BarGap) / _bars.Count;
        if (barWidth < 2) barWidth = 2;
        double usableHeight = h - TopPadding - BottomPadding;

        for (int i = 0; i < _bars.Count; i++)
        {
            var bar = _bars[i];
            double x = i * (barWidth + BarGap);
            int total = bar.Accepted + bar.Native + bar.Dismissed;
            if (total <= 0) continue;

            double barHeight = (double)total / maxTotal * usableHeight;
            double y = h - BottomPadding;

            // Today glow background
            if (bar.IsToday)
            {
                var glow = new Rectangle
                {
                    Width = barWidth + 2,
                    Height = barHeight + 4,
                    Fill = new SolidColorBrush(TodayGlow),
                    RadiusX = 3,
                    RadiusY = 3,
                    IsHitTestVisible = false
                };
                SetLeft(glow, x - 1);
                SetTop(glow, y - barHeight - 2);
                Children.Add(glow);
            }

            // Draw segments bottom-to-top: accepted, native, dismissed
            DrawSegment(x, ref y, barWidth, bar.Accepted, maxTotal, usableHeight, AcceptColor);
            DrawSegment(x, ref y, barWidth, bar.Native, maxTotal, usableHeight, NativeColor);
            DrawSegment(x, ref y, barWidth, bar.Dismissed, maxTotal, usableHeight, DismissColor);
        }
    }

    private void DrawSegment(double x, ref double y, double barWidth,
        int count, int maxTotal, double usableHeight, Color color)
    {
        if (count <= 0) return;

        double segHeight = (double)count / maxTotal * usableHeight;
        if (segHeight < 1) segHeight = 1;

        var rect = new Rectangle
        {
            Width = barWidth,
            Height = segHeight,
            Fill = new SolidColorBrush(color),
            RadiusX = 2,
            RadiusY = 2,
            IsHitTestVisible = false
        };

        y -= segHeight;
        SetLeft(rect, x);
        SetTop(rect, y);
        Children.Add(rect);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_bars.Count == 0) return;

        var pos = e.GetPosition(this);
        var w = ActualWidth > 0 ? ActualWidth : Width;
        if (w <= 0) return;

        double barWidth = (w - (_bars.Count - 1) * BarGap) / _bars.Count;
        int index = (int)(pos.X / (barWidth + BarGap));
        index = Math.Clamp(index, 0, _bars.Count - 1);

        var bar = _bars[index];
        _tooltip.Content = $"{bar.Label}\n" +
                           $"{bar.Accepted} accepted, {bar.Native} native, {bar.Dismissed} dismissed";
        ToolTip = _tooltip;
        _tooltip.IsOpen = true;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _tooltip.IsOpen = false;
    }
}
