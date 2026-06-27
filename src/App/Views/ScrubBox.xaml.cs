using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ModelCodex.App.Views;

/// <summary>A tiny numeric field you scrub by dragging horizontally (right = increase). Two-way bound to
/// <see cref="Value"/>; double-click resets nothing here — drag is the interaction.</summary>
public partial class ScrubBox : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ScrubBox),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty SensitivityProperty = DependencyProperty.Register(
        nameof(Sensitivity), typeof(double), typeof(ScrubBox), new PropertyMetadata(0.01));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Sensitivity { get => (double)GetValue(SensitivityProperty); set => SetValue(SensitivityProperty, value); }

    private Point _last;
    private bool _drag;

    public ScrubBox()
    {
        InitializeComponent();
        Render();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ScrubBox)d).Render();

    private void Render() => Txt.Text = Value.ToString("0.###", CultureInfo.InvariantCulture);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _drag = true; _last = e.GetPosition(this); CaptureMouse(); e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_drag) return;
        var p = e.GetPosition(this);
        double dx = p.X - _last.X;
        _last = p;
        // Finer steps on slow drags; larger values scale step so big channels are still draggable.
        double scale = Math.Max(1.0, Math.Abs(Value)) * Sensitivity;
        Value += dx * scale;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _drag = false; ReleaseMouseCapture();
    }
}
