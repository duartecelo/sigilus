using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Sigilus.Core.Domain;

namespace Sigilus.Ui.Wpf.Controls;

/// <summary>
/// Canvas que desenha retângulos de redação sobre a página renderizada,
/// suportando: clique para alternar aprovação, arrasto no fundo para
/// criar tarja manual, e <b>resize por 4 cantinhos</b> quando o mouse
/// está sobre um retângulo.
/// </summary>
public sealed class RedactionOverlay : Canvas
{
    public static readonly DependencyProperty PageWidthPtsProperty =
        DependencyProperty.Register(nameof(PageWidthPts), typeof(double), typeof(RedactionOverlay),
            new PropertyMetadata(0d, (d, _) => ((RedactionOverlay)d).Redraw()));

    public static readonly DependencyProperty PageHeightPtsProperty =
        DependencyProperty.Register(nameof(PageHeightPts), typeof(double), typeof(RedactionOverlay),
            new PropertyMetadata(0d, (d, _) => ((RedactionOverlay)d).Redraw()));

    public double PageWidthPts
    {
        get => (double)GetValue(PageWidthPtsProperty);
        set => SetValue(PageWidthPtsProperty, value);
    }

    public double PageHeightPts
    {
        get => (double)GetValue(PageHeightPtsProperty);
        set => SetValue(PageHeightPtsProperty, value);
    }

    public ObservableCollection<RedactionDecision> Decisions { get; } = new();

    /// <summary>
    /// Função chamada quando um retângulo manual é criado/editado. Recebe
    /// o rect proposto e devolve o rect final ajustado (snap a palavra OCR
    /// + NER se disponível). Se null, nenhum snap é feito.
    /// </summary>
    public Func<PdfRect, PdfRect>? SnapFn { get; set; }

    private Point? _dragStart;
    private Rectangle? _dragRect;

    // resize state
    private int? _resizeDecisionIndex;
    private ResizeHandle _resizeHandle = ResizeHandle.None;
    private PdfRect _resizeStartBounds;
    private Point _resizeStartMouse;

    private enum ResizeHandle { None, TopLeft, TopRight, BottomLeft, BottomRight }

    private const double HandleSize = 10;

    public RedactionOverlay()
    {
        Background = Brushes.Transparent;
        Decisions.CollectionChanged += OnDecisionsChanged;
        SizeChanged += (_, _) => Redraw();
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
    }

    private void OnDecisionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    public void Redraw()
    {
        Children.Clear();
        if (PageWidthPts <= 0 || PageHeightPts <= 0 || ActualWidth <= 0) return;
        var scale = ActualWidth / PageWidthPts;

        for (var i = 0; i < Decisions.Count; i++)
        {
            var d = Decisions[i];
            var (x, y, w, h) = ToDips(d.Bounds, scale);
            var rect = new Rectangle
            {
                Width = w,
                Height = h,
                Stroke = d.Approved ? Brushes.Red : Brushes.Orange,
                StrokeThickness = 1.5,
                Fill = d.Approved ? new SolidColorBrush(Color.FromArgb(96, 255, 0, 0))
                                  : new SolidColorBrush(Color.FromArgb(48, 255, 165, 0)),
                Cursor = Cursors.Hand,
                Tag = i,
            };
            rect.MouseLeftButtonDown += OnRectClicked;
            rect.MouseEnter += (s, _) => ShowHandlesFor((int)((Rectangle)s).Tag);
            SetLeft(rect, x); SetTop(rect, y);
            Children.Add(rect);
        }
    }

    private void OnRectClicked(object sender, MouseButtonEventArgs e)
    {
        if (_resizeHandle != ResizeHandle.None) return;   // priorize resize
        if (sender is not Rectangle r || r.Tag is not int idx) return;
        if (idx < 0 || idx >= Decisions.Count) return;
        var dec = Decisions[idx];
        Decisions[idx] = dec with { Approved = !dec.Approved };
        e.Handled = true;
    }

    private void ShowHandlesFor(int idx)
    {
        // Remove handles antigos.
        for (var i = Children.Count - 1; i >= 0; i--)
            if (Children[i] is Rectangle r && r.Tag is HandleTag) Children.RemoveAt(i);

        if (idx < 0 || idx >= Decisions.Count) return;
        var scale = ActualWidth / PageWidthPts;
        var (x, y, w, h) = ToDips(Decisions[idx].Bounds, scale);

        AddHandle(x, y, ResizeHandle.TopLeft, idx, Cursors.SizeNWSE);
        AddHandle(x + w - HandleSize, y, ResizeHandle.TopRight, idx, Cursors.SizeNESW);
        AddHandle(x, y + h - HandleSize, ResizeHandle.BottomLeft, idx, Cursors.SizeNESW);
        AddHandle(x + w - HandleSize, y + h - HandleSize, ResizeHandle.BottomRight, idx, Cursors.SizeNWSE);
    }

    private sealed class HandleTag
    {
        public int DecisionIndex;
        public ResizeHandle Handle;
    }

    private void AddHandle(double x, double y, ResizeHandle which, int decisionIdx, Cursor cur)
    {
        var h = new Rectangle
        {
            Width = HandleSize, Height = HandleSize,
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            Cursor = cur,
            Tag = new HandleTag { DecisionIndex = decisionIdx, Handle = which },
        };
        h.MouseLeftButtonDown += OnHandleDown;
        SetLeft(h, x); SetTop(h, y);
        Children.Add(h);
    }

    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Rectangle r || r.Tag is not HandleTag tag) return;
        _resizeDecisionIndex = tag.DecisionIndex;
        _resizeHandle = tag.Handle;
        _resizeStartBounds = Decisions[tag.DecisionIndex].Bounds;
        _resizeStartMouse = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != this) return;
        _dragStart = e.GetPosition(this);
        _dragRect = new Rectangle
        {
            Stroke = Brushes.Red, StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(96, 255, 0, 0)),
        };
        Children.Add(_dragRect);
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_resizeHandle != ResizeHandle.None && _resizeDecisionIndex is int idx)
        {
            var scale = ActualWidth / PageWidthPts;
            var mouse = e.GetPosition(this);
            var dxDip = mouse.X - _resizeStartMouse.X;
            var dyDip = mouse.Y - _resizeStartMouse.Y;
            // Em DIP: x cresce → direita; em PDF user-space, X cresce → direita, Y cresce → cima.
            // dy DIP positivo significa Y PDF decrescente.
            var dx = (float)(dxDip / scale);
            var dy = -(float)(dyDip / scale);
            var b = _resizeStartBounds;
            var newRect = _resizeHandle switch
            {
                ResizeHandle.TopRight => new PdfRect(b.X, b.Y, b.Width + dx, b.Height + dy),
                ResizeHandle.TopLeft => new PdfRect(b.X + dx, b.Y, b.Width - dx, b.Height + dy),
                ResizeHandle.BottomRight => new PdfRect(b.X, b.Y + dy, b.Width + dx, b.Height - dy),
                ResizeHandle.BottomLeft => new PdfRect(b.X + dx, b.Y + dy, b.Width - dx, b.Height - dy),
                _ => b,
            };
            if (newRect.Width < 2 || newRect.Height < 2) return;
            Decisions[idx] = Decisions[idx] with { Bounds = newRect };
            ShowHandlesFor(idx);   // recoloca handles
            return;
        }

        if (_dragStart is Point start && _dragRect is not null)
        {
            var p = e.GetPosition(this);
            var x = Math.Min(start.X, p.X);
            var y = Math.Min(start.Y, p.Y);
            SetLeft(_dragRect, x); SetTop(_dragRect, y);
            _dragRect.Width = Math.Abs(p.X - start.X);
            _dragRect.Height = Math.Abs(p.Y - start.Y);
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizeHandle != ResizeHandle.None)
        {
            _resizeHandle = ResizeHandle.None;
            _resizeDecisionIndex = null;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_dragStart is null || _dragRect is null) return;
        var x = GetLeft(_dragRect); var y = GetTop(_dragRect);
        var w = _dragRect.Width; var h = _dragRect.Height;
        Children.Remove(_dragRect);
        _dragStart = null; _dragRect = null;
        ReleaseMouseCapture();
        if (w < 4 || h < 4) return;

        var scale = ActualWidth / PageWidthPts;
        var pdfBounds = FromDips(x, y, w, h, scale);
        if (SnapFn is not null)
        {
            var snapped = SnapFn(pdfBounds);
            if (!snapped.IsEmpty) pdfBounds = snapped;
        }

        Decisions.Add(new RedactionDecision(pdfBounds, 0, Approved: true, Reason: "manual", Origin: null));
    }

    private (double x, double y, double w, double h) ToDips(PdfRect r, double scale)
        => (r.X * scale, (PageHeightPts - (r.Y + r.Height)) * scale, r.Width * scale, r.Height * scale);

    private PdfRect FromDips(double x, double y, double w, double h, double scale)
    {
        var pdfX = (float)(x / scale);
        var pdfW = (float)(w / scale);
        var pdfH = (float)(h / scale);
        var pdfY = (float)(PageHeightPts - y / scale - pdfH);
        return new PdfRect(pdfX, pdfY, pdfW, pdfH);
    }
}
