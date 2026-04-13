using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KeystrokeApp.Controls;

/// <summary>
/// Lightweight sparkline chart rendered on a Canvas. Supports multiple data series,
/// gradient fill below the line, optional data-point dots, and hover tooltips.
/// Designed for the dark Keystroke Settings theme.
/// </summary>
public class SparklineControl : Canvas
{
    public record DataPoint(string Label, double Value);

    public record Series(string Name, List<DataPoint> Points, Color LineColor);

    private readonly List<Series> _series = new();
    private double _minValue;
    private double _maxValue = 100;
    private readonly ToolTip _tooltip = new()
    {
        Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
        Foreground = new SolidColorBrush(Color.FromRgb(240, 246, 252)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61)),
        FontSize = 11,
        Padding = new Thickness(8, 4, 8, 4)
    };

    private Ellipse? _hoverDot;
    private const double Padding = 4;

    public SparklineControl()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
    }

    public void SetRange(double min, double max)
    {
        _minValue = min;
        _maxValue = max;
    }

    public void ClearSeries()
    {
        _series.Clear();
        Children.Clear();
    }

    public void AddSeries(string name, List<DataPoint> points, Color lineColor)
    {
        _series.Add(new Series(name, points, lineColor));
    }

    public void Render()
    {
        Children.Clear();
        if (_series.Count == 0) return;

        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        if (w <= 0 || h <= 0) return;

        foreach (var series in _series)
        {
            if (series.Points.Count == 0) continue;

            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(series.LineColor),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
            };

            // Build gradient fill polygon
            var fillPolygon = new Polygon
            {
                Fill = new LinearGradientBrush(
                    Color.FromArgb(50, series.LineColor.R, series.LineColor.G, series.LineColor.B),
                    Color.FromArgb(5, series.LineColor.R, series.LineColor.G, series.LineColor.B),
                    90),
                StrokeThickness = 0
            };

            int count = series.Points.Count;
            double xStep = count > 1 ? (w - 2 * Padding) / (count - 1) : 0;
            double range = _maxValue - _minValue;
            if (range <= 0) range = 1;

            var linePoints = new PointCollection();
            var fillPoints = new PointCollection();

            for (int i = 0; i < count; i++)
            {
                double x = Padding + i * xStep;
                double y = h - Padding - ((series.Points[i].Value - _minValue) / range * (h - 2 * Padding));
                y = Math.Clamp(y, Padding, h - Padding);
                linePoints.Add(new Point(x, y));
                fillPoints.Add(new Point(x, y));
            }

            // Close the fill polygon along the bottom
            fillPoints.Add(new Point(Padding + (count - 1) * xStep, h - Padding));
            fillPoints.Add(new Point(Padding, h - Padding));

            fillPolygon.Points = fillPoints;
            polyline.Points = linePoints;

            Children.Add(fillPolygon);
            Children.Add(polyline);
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_series.Count == 0 || _series[0].Points.Count == 0) return;

        var pos = e.GetPosition(this);
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        if (w <= 0 || h <= 0) return;

        int count = _series[0].Points.Count;
        double xStep = count > 1 ? (w - 2 * Padding) / (count - 1) : 0;
        int index = xStep > 0 ? (int)Math.Round((pos.X - Padding) / xStep) : 0;
        index = Math.Clamp(index, 0, count - 1);

        // Build tooltip text from all series at this index
        var lines = new List<string>();
        lines.Add(_series[0].Points[index].Label);
        foreach (var series in _series)
        {
            if (index < series.Points.Count)
                lines.Add($"{series.Name}: {series.Points[index].Value:F0}");
        }

        _tooltip.Content = string.Join("\n", lines);
        ToolTip = _tooltip;
        _tooltip.IsOpen = true;

        // Show hover dot on first series
        double range = _maxValue - _minValue;
        if (range <= 0) range = 1;
        double dotX = Padding + index * xStep;
        double dotY = h - Padding - ((_series[0].Points[index].Value - _minValue) / range * (h - 2 * Padding));
        dotY = Math.Clamp(dotY, Padding, h - Padding);

        if (_hoverDot == null)
        {
            _hoverDot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = new SolidColorBrush(_series[0].LineColor),
                IsHitTestVisible = false
            };
            Children.Add(_hoverDot);
        }
        SetLeft(_hoverDot, dotX - 3);
        SetTop(_hoverDot, dotY - 3);
        _hoverDot.Visibility = Visibility.Visible;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _tooltip.IsOpen = false;
        if (_hoverDot != null)
            _hoverDot.Visibility = Visibility.Collapsed;
    }
}
