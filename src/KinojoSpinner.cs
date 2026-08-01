using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace KinojoMeterPrototype
{
    // WPF equivalent of web .kinojo-spinner: 18px white ring + blue conic train.
    internal sealed class KinojoSpinner : Grid
    {
        private readonly RotateTransform _rotation = new RotateTransform();
        private readonly DispatcherTimer _timer;
        private DateTime _cycleStartedUtc;

        public KinojoSpinner()
        {
            Width = 18;
            Height = 18;
            Children.Add(new Ellipse { Stroke = Brushes.White, StrokeThickness = 4 });
            var train = new Ellipse
            {
                Stroke = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                StrokeThickness = 4,
                StrokeDashArray = new DoubleCollection { 2.3, 9.2 },
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _rotation
            };
            Children.Add(train);
            _cycleStartedUtc = DateTime.UtcNow;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _timer.Tick += delegate
            {
                var progress = (DateTime.UtcNow - _cycleStartedUtc).TotalMilliseconds / 1050.0;
                progress -= Math.Floor(progress);
                // Close visual approximation of cubic-bezier(.55,.08,.25,.98).
                var eased = progress < 0.46
                    ? 205.0 / 360.0 * Smooth(progress / 0.46)
                    : 205.0 / 360.0 + 155.0 / 360.0 * Smooth((progress - 0.46) / 0.54);
                _rotation.Angle = eased * 360.0;
            };
            Loaded += delegate { _cycleStartedUtc = DateTime.UtcNow; _timer.Start(); };
            Unloaded += delegate { _timer.Stop(); };
        }

        private static double Smooth(double value) { return value * value * (3.0 - 2.0 * value); }
    }
}
