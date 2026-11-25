using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AetherBrowser.Controls;

public sealed class LucideIcon : Viewbox
{
    public static readonly DependencyProperty IconKeyProperty =
        DependencyProperty.Register(nameof(IconKey), typeof(string), typeof(LucideIcon),
            new PropertyMetadata("", OnIconKeyChanged));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(LucideIcon),
            new PropertyMetadata(15.0, OnSizeChanged));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(LucideIcon),
            new PropertyMetadata(1.8, OnStrokeChanged));

    public static readonly DependencyProperty IconBrushProperty =
        DependencyProperty.Register(nameof(IconBrush), typeof(Brush), typeof(LucideIcon),
            new PropertyMetadata(null, OnBrushChanged));

    public static readonly DependencyProperty FillBrushProperty =
        DependencyProperty.Register(nameof(FillBrush), typeof(Brush), typeof(LucideIcon),
            new PropertyMetadata(Brushes.Transparent, OnFillBrushChanged));

    private readonly Path _path;

    public string IconKey
    {
        get => (string)GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public Brush? IconBrush
    {
        get => (Brush?)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public LucideIcon()
    {
        Width = 15;
        Height = 15;
        Stretch = Stretch.Uniform;

        var canvas = new Canvas { Width = 24, Height = 24 };
        _path = new Path
        {
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent
        };
        _path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(
                System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1)
        });

        canvas.Children.Add(_path);
        Child = canvas;
    }

    private static void OnIconKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LucideIcon ico && e.NewValue is string key && !string.IsNullOrEmpty(key))
        {
            if (Application.Current?.TryFindResource(key) is Geometry geom)
                ico._path.Data = geom;
        }
    }

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LucideIcon ico && e.NewValue is double sz)
        {
            ico.Width = sz;
            ico.Height = sz;
        }
    }

    private static void OnStrokeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LucideIcon ico && e.NewValue is double st)
            ico._path.StrokeThickness = st;
    }

    private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LucideIcon ico && e.NewValue is Brush b)
            ico._path.Stroke = b;
    }

    private static void OnFillBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LucideIcon ico)
            ico._path.Fill = e.NewValue as Brush ?? Brushes.Transparent;
    }
}
