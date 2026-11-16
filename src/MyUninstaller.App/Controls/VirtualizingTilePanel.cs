using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MyUninstaller.App.Controls;

/// <summary>
/// Pengganti WrapPanel untuk kisi ikon besar: ubin berukuran tetap disusun per baris, tetapi HANYA ubin yang terlihat
/// (plus satu baris cadangan atas/bawah) yang dibuat. WrapPanel biasa membuat semua ubin (±430 beserta ikonnya) sekaligus,
/// sehingga jendela lambat tampil dan memori besar.
/// </summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(130d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(132d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private const double WheelStep = 3 * 38;

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _columns = 1;

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        // WPF baru menyiapkan ItemContainerGenerator setelah InternalChildren diakses pertama kali.
        _ = InternalChildren;
        double width = double.IsInfinity(availableSize.Width) ? ItemWidth * 6 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? ItemHeight * 4 : availableSize.Height;
        _columns = Math.Max(1, (int)(width / ItemWidth));
        int count = ItemCount;
        int rows = (count + _columns - 1) / _columns;
        UpdateScrollInfo(new Size(width, rows * ItemHeight), new Size(width, height));

        // Rentang yang terlihat + satu baris cadangan di atas dan bawah (gulir terasa mulus).
        int firstRow = Math.Max(0, (int)(_offset.Y / ItemHeight) - 1);
        int lastRow = Math.Min(rows - 1, (int)((_offset.Y + height) / ItemHeight) + 1);
        int first = firstRow * _columns;
        int last = Math.Min(count - 1, ((lastRow + 1) * _columns) - 1);

        var generator = ItemContainerGenerator;
        if (count > 0 && first <= last)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
            {
                for (int index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out bool isNew);
                    if (isNew)
                    {
                        if (childIndex >= InternalChildren.Count)
                        {
                            AddInternalChild(child);
                        }
                        else
                        {
                            InsertInternalChild(childIndex, child);
                        }

                        generator.PrepareItemContainer(child);
                    }

                    child.Measure(new Size(ItemWidth, ItemHeight));
                }
            }
        }

        CleanUp(first, last);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            int row = index / _columns;
            int column = index % _columns;
            InternalChildren[i].Arrange(new Rect(column * ItemWidth - _offset.X, row * ItemHeight - _offset.Y, ItemWidth, ItemHeight));
        }

        return finalSize;
    }

    /// <summary>Membuang ubin di luar rentang terlihat (kecuali yang sedang punya fokus keyboard).</summary>
    private void CleanUp(int first, int last)
    {
        var generator = ItemContainerGenerator;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int index = generator.IndexFromGeneratorPosition(position);
            if ((index < first || index > last) && !InternalChildren[i].IsKeyboardFocusWithin)
            {
                generator.Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                SetVerticalOffset(_offset.Y);
                break;
        }
    }

    /// <summary>Dipakai ListBox.ScrollIntoView untuk ubin yang belum dibuat (Mode Pemburu, pencarian).</summary>
    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || _columns <= 0)
        {
            return;
        }

        double top = index / _columns * ItemHeight;
        if (top < _offset.Y)
        {
            SetVerticalOffset(top);
        }
        else if (top + ItemHeight > _offset.Y + _viewport.Height)
        {
            SetVerticalOffset(top + ItemHeight - _viewport.Height);
        }
    }

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        if (extent == _extent && viewport == _viewport)
        {
            return;
        }

        _extent = extent;
        _viewport = viewport;
        _offset.Y = Math.Clamp(_offset.Y, 0, Math.Max(0, _extent.Height - _viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();
    }

    // ---------- IScrollInfo (gulir vertikal saja) ----------

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => _offset.X;

    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offset.Y - ItemHeight / 3);

    public void LineDown() => SetVerticalOffset(_offset.Y + ItemHeight / 3);

    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);

    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - WheelStep);

    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + WheelStep);

    public void LineLeft()
    {
    }

    public void LineRight()
    {
    }

    public void PageLeft()
    {
    }

    public void PageRight()
    {
    }

    public void MouseWheelLeft()
    {
    }

    public void MouseWheelRight()
    {
    }

    public void SetHorizontalOffset(double offset)
    {
    }

    public void SetVerticalOffset(double offset)
    {
        double clamped = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (double.IsNaN(clamped) || clamped == _offset.Y)
        {
            return;
        }

        _offset.Y = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is UIElement element && InternalChildren.IndexOf(element) is var child and >= 0)
        {
            int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(child, 0));
            BringIndexIntoView(index);
        }

        return rectangle;
    }
}
