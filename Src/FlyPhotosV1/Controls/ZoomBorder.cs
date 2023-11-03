using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlyPhotos.Controls;

public class ZoomBorder : Border
{
    private UIElement? _child;
    private Point _origin;
    private Point _start;

    public override UIElement Child
    {
        get => base.Child;
        set
        {
            if (value != Child)
                Initialize(value);
            base.Child = value;
        }
    }

    private static TranslateTransform GetTranslateTransform(UIElement element)
    {
        return (TranslateTransform)((TransformGroup)element.RenderTransform)
            .Children.First(tr => tr is TranslateTransform);
    }

    private static ScaleTransform GetScaleTransform(UIElement element)
    {
        return (ScaleTransform)((TransformGroup)element.RenderTransform)
            .Children.First(tr => tr is ScaleTransform);
    }

    public void Initialize(UIElement element)
    {
        _child = element;
        if (_child != null)
        {
            var group = new TransformGroup();
            var st = new ScaleTransform();
            group.Children.Add(st);
            var tt = new TranslateTransform();
            group.Children.Add(tt);
            _child.RenderTransform = group;
            _child.RenderTransformOrigin = new Point(0.0, 0.0);
            MouseWheel += child_MouseWheel;
            MouseLeftButtonDown += child_MouseLeftButtonDown;
            MouseLeftButtonUp += child_MouseLeftButtonUp;
            MouseMove += child_MouseMove;
            PreviewMouseRightButtonDown += child_PreviewMouseRightButtonDown;
        }
    }

    public void Reset()
    {
        if (_child != null)
        {
            // reset zoom
            var st = GetScaleTransform(_child);
            st.ScaleX = 1.0;
            st.ScaleY = 1.0;

            // reset pan
            var tt = GetTranslateTransform(_child);
            tt.X = 0.0;
            tt.Y = 0.0;
        }
    }

    #region Child Events

    private void child_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_child != null)
        {
            var st = GetScaleTransform(_child);
            var tt = GetTranslateTransform(_child);

            var zoom = e.Delta > 0 ? .2 : -.2;
            if (!(e.Delta > 0) && (st.ScaleX < .4 || st.ScaleY < .4))
                return;

            var relative = e.GetPosition(_child);

            var absoluteX = relative.X * st.ScaleX + tt.X;
            var absoluteY = relative.Y * st.ScaleY + tt.Y;

            st.ScaleX += zoom;
            st.ScaleY += zoom;

            tt.X = absoluteX - relative.X * st.ScaleX;
            tt.Y = absoluteY - relative.Y * st.ScaleY;
        }
    }

    private void child_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != this)
        {
            if (e is { ChangedButton: MouseButton.Left, ClickCount: 2 })
            {
                Reset();
                return;
            }

            if (_child != null)
            {
                var tt = GetTranslateTransform(_child);
                _start = e.GetPosition(this);
                _origin = new Point(tt.X, tt.Y);
                Cursor = Cursors.Hand;
                _child.CaptureMouse();
            }
        }
        else
        {
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow is { WindowState: WindowState.Maximized })
            {
                Reset();
                mainWindow.WindowState = WindowState.Normal;
            }
        }
    }

    private void child_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_child != null)
        {
            _child.ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
        }
    }

    private void child_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        Reset();
    }

    private void child_MouseMove(object sender, MouseEventArgs e)
    {
        if (_child is { IsMouseCaptured: true })
        {
            var tt = GetTranslateTransform(_child);
            var v = _start - e.GetPosition(this);
            tt.X = _origin.X - v.X;
            tt.Y = _origin.Y - v.Y;
        }
    }

    #endregion
}