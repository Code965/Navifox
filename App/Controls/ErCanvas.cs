using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Navifox.App.ViewModels;

namespace Navifox.App.Controls;

/// <summary>
/// Superficie del diagramma ER: riquadri tabella con colonne (PK/FK), relazioni con terminali «a zampa di gallina»,
/// trascinamento dei riquadri, pan con il trascinamento dello sfondo, zoom con la rotella.
/// </summary>
public sealed class ErCanvas : Control
{
    private const double HeaderHeight = 32, RowHeight = 21, PaddingX = 10, Radius = 8;
    private ModelTabViewModel? _model;
    private Vector _offset = new(20, 20);
    private double _zoom = 1;
    private DiagramTable? _dragTable;
    private Point _dragStart;
    private Point _dragOrigin;
    private bool _panning;
    private Vector _panOrigin;
    private bool _moved;
    // Nuova relazione in corso: colonna di partenza e posizione attuale del puntatore (coordinate del modello).
    private (DiagramTable Table, string Column)? _link;
    private Point _linkPoint;
    private (DiagramTable Table, string? Column)? _linkTarget;
    private readonly Typeface _typeface = new("Inter, Segoe UI, sans-serif");
    private readonly Typeface _bold = new("Inter, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.SemiBold);
    private readonly Typeface _mono = new("Cascadia Mono, Consolas, monospace");

    public ErCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public ModelTabViewModel? Model
    {
        get => _model;
        set
        {
            if (_model is not null) _model.DiagramChanged -= OnDiagramChanged;
            _model = value;
            if (_model is not null) _model.DiagramChanged += OnDiagramChanged;
            if (_model?.Tables.Count > 0) OnDiagramChanged(true);
            InvalidateVisual();
        }
    }

    private void OnDiagramChanged(bool relayout)
    {
        if (_model is null) return;
        if (relayout)
        {
            if (!_model.ApplySavedLayout(Measure)) _model.AutoLayout(Measure);
            Avalonia.Threading.Dispatcher.UIThread.Post(Fit, Avalonia.Threading.DispatcherPriority.Background);
        }
        else
        {
            foreach (var table in _model.Tables) table.Width = Measure(table).Width;
            if (_model.SelectedTable is { } selected && !string.IsNullOrWhiteSpace(_model.SearchText)) CenterOn(selected);
        }
        InvalidateVisual();
    }

    // ───────────── API per la vista ─────────────

    public void ZoomIn() => ZoomAround(new Point(Bounds.Width / 2, Bounds.Height / 2), 1.2);
    public void ZoomOut() => ZoomAround(new Point(Bounds.Width / 2, Bounds.Height / 2), 1 / 1.2);

    public void Relayout()
    {
        _model?.ForgetLayout();
        if (_model is null) return;
        _model.AutoLayout(Measure);
        Fit();
    }

    public void Fit()
    {
        if (_model is null || _model.Tables.Count == 0 || Bounds.Width <= 0) return;
        var bounds = ContentBounds();
        _zoom = Math.Clamp(Math.Min((Bounds.Width - 40) / bounds.Width, (Bounds.Height - 40) / bounds.Height), 0.15, 1.2);
        _offset = new Vector((Bounds.Width - bounds.Width * _zoom) / 2 - bounds.X * _zoom, (Bounds.Height - bounds.Height * _zoom) / 2 - bounds.Y * _zoom);
        SyncZoom();
        InvalidateVisual();
    }

    public void ExportPng(string path)
    {
        if (_model is null || _model.Tables.Count == 0) return;
        var bounds = ContentBounds().Inflate(30);
        var scale = Math.Min(2, 8000 / Math.Max(bounds.Width, bounds.Height));
        var size = new PixelSize((int)(bounds.Width * scale), (int)(bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        var (oldOffset, oldZoom) = (_offset, _zoom);
        _offset = new Vector(-bounds.X, -bounds.Y);
        _zoom = 1;
        using (var context = bitmap.CreateDrawingContext())
            DrawScene(context, new Rect(0, 0, bounds.Width, bounds.Height));
        (_offset, _zoom) = (oldOffset, oldZoom);
        bitmap.Save(path);
    }

    private void CenterOn(DiagramTable table)
    {
        var size = Measure(table);
        _offset = new Vector(Bounds.Width / 2 - (table.X + size.Width / 2) * _zoom, Bounds.Height / 2 - (table.Y + size.Height / 2) * _zoom);
    }

    private Rect ContentBounds()
    {
        var tables = _model!.Tables;
        double minX = tables.Min(t => t.X), minY = tables.Min(t => t.Y);
        double maxX = tables.Max(t => t.X + Measure(t).Width), maxY = tables.Max(t => t.Y + Measure(t).Height);
        return new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    private void ZoomAround(Point screen, double factor)
    {
        var newZoom = Math.Clamp(_zoom * factor, 0.15, 3);
        var world = (screen - _offset) / _zoom;
        _zoom = newZoom;
        _offset = (Vector)screen - world * _zoom;
        SyncZoom();
        InvalidateVisual();
    }

    private void SyncZoom()
    {
        if (_model is not null) _model.Zoom = _zoom;
    }

    // ───────────── Misure ─────────────

    public (double Width, double Height) Measure(DiagramTable table)
    {
        var columns = _model?.VisibleColumns(table) ?? table.AllColumns;
        var showTypes = _model?.ShowTypes ?? true;
        var width = Text(table.Name, 13, _bold, Brushes.Black).Width + PaddingX * 2 + 26;
        foreach (var column in columns)
        {
            var w = 22 + Text(column.Name, 12, _typeface, Brushes.Black).Width + PaddingX * 2;
            if (showTypes) w += 18 + Text(ShortType(column.Type), 11, _mono, Brushes.Black).Width;
            width = Math.Max(width, w);
        }
        return (Math.Clamp(width, 150, 420), HeaderHeight + Math.Max(1, columns.Count) * RowHeight + 6);
    }

    private static string ShortType(string type) => type.Length > 28 ? type[..27] + "…" : type;

    private FormattedText Text(string text, double size, Typeface typeface, IBrush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush);

    // ───────────── Disegno ─────────────

    public override void Render(DrawingContext context) => DrawScene(context, new Rect(Bounds.Size));

    private IBrush Res(string key, string fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : new SolidColorBrush(Color.Parse(fallback));

    private void DrawScene(DrawingContext context, Rect viewport)
    {
        var background = Res("InsetBrush", "#FAF8F5");
        var surface = Res("SurfaceBrush", "#FFFFFF");
        var border = Res("BorderBrush", "#E3DED7");
        var accent = Res("AccentBrush", "#E8590C");
        var accentSoft = Res("AccentSoftBrush", "#FFEDE2");
        var subtle = Res("SubtleSurfaceBrush", "#F1EEEA");
        var primary = Res("PrimaryTextBrush", "#1D1B19");
        var body = Res("BodyTextBrush", "#3D3934");
        var muted = Res("MutedTextBrush", "#9BA1A9");
        var secondary = Res("SecondaryTextBrush", "#625C56");

        context.FillRectangle(background, viewport);
        // griglia a puntini
        var step = 22 * _zoom;
        if (step >= 8)
        {
            var dot = new SolidColorBrush(((ISolidColorBrush)border).Color, 0.9);
            for (var x = _offset.X % step; x < viewport.Width; x += step)
                for (var y = _offset.Y % step; y < viewport.Height; y += step)
                    context.FillRectangle(dot, new Rect(x, y, 1.3, 1.3));
        }
        if (_model is null || _model.Tables.Count == 0) return;

        using (context.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_offset.X, _offset.Y)))
        {
            var selected = _model.SelectedTable;
            var linePen = new Pen(new SolidColorBrush(((ISolidColorBrush)secondary).Color, 0.55), 1.4);
            var activePen = new Pen(accent, 2.2);
            foreach (var relation in _model.Relations.OrderBy(r => r.From == selected || r.To == selected))
                DrawRelation(context, relation, relation.From == selected || relation.To == selected ? activePen : linePen);
            if (_link is { } link)
            {
                var (w, _) = Measure(link.Table);
                var from = new Point(_linkPoint.X >= link.Table.X + w / 2 ? link.Table.X + w : link.Table.X, RowCenter(link.Table, link.Column));
                var to = _linkTarget is { } target ? new Point(target.Table.X + (from.X > target.Table.X ? Measure(target.Table).Width : 0),
                        target.Column is null ? target.Table.Y + HeaderHeight / 2 : RowCenter(target.Table, target.Column)) : _linkPoint;
                context.DrawLine(new Pen(accent, 2, new DashStyle([4, 3], 0)), from, to);
                context.DrawEllipse(accent, null, to, 4, 4);
                if (_linkTarget is { } t)
                {
                    var (tw, th) = Measure(t.Table);
                    context.DrawRectangle(null, new Pen(accent, 2.5), new Rect(t.Table.X - 3, t.Table.Y - 3, tw + 6, th + 6), Radius + 2, Radius + 2);
                }
            }

            foreach (var table in _model.Tables)
            {
                var (width, height) = Measure(table);
                var rect = new Rect(table.X, table.Y, width, height);
                var isSelected = table == selected;
                var isMatch = !string.IsNullOrWhiteSpace(_model.SearchText) && table.Name.Contains(_model.SearchText, StringComparison.OrdinalIgnoreCase);
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(28, 0, 0, 0)), null, rect.Translate(new Vector(0, 3)), Radius, Radius);
                context.DrawRectangle(surface, new Pen(isSelected || isMatch ? accent : border, isSelected ? 2 : 1), rect, Radius, Radius);
                // intestazione
                var header = new StreamGeometry();
                using (var g = header.Open())
                {
                    g.BeginFigure(new Point(rect.X, rect.Y + HeaderHeight), true);
                    g.LineTo(new Point(rect.X, rect.Y + Radius));
                    g.ArcTo(new Point(rect.X + Radius, rect.Y), new Size(Radius, Radius), 0, false, SweepDirection.Clockwise);
                    g.LineTo(new Point(rect.Right - Radius, rect.Y));
                    g.ArcTo(new Point(rect.Right, rect.Y + Radius), new Size(Radius, Radius), 0, false, SweepDirection.Clockwise);
                    g.LineTo(new Point(rect.Right, rect.Y + HeaderHeight));
                    g.EndFigure(true);
                }
                context.DrawGeometry(table.IsView ? subtle : accentSoft, null, header);
                context.DrawLine(new Pen(border, 1), new Point(rect.X, rect.Y + HeaderHeight), new Point(rect.Right, rect.Y + HeaderHeight));
                DrawIcon(context, table.IsView ? "eye" : "table", new Point(rect.X + PaddingX, rect.Y + 9), 14, table.IsView ? secondary : accent);
                var title = Text(table.Name, 13, _bold, table.IsView ? body : accent);
                title.MaxTextWidth = width - PaddingX * 2 - 22;
                title.Trimming = TextTrimming.CharacterEllipsis;
                title.MaxLineCount = 1;
                context.DrawText(title, new Point(rect.X + PaddingX + 22, rect.Y + (HeaderHeight - title.Height) / 2));

                var columns = _model.VisibleColumns(table);
                var y = rect.Y + HeaderHeight + 3;
                foreach (var column in columns)
                {
                    if (column.IsPrimaryKey) DrawIcon(context, "key", new Point(rect.X + PaddingX, y + 4), 12, accent);
                    else if (column.IsForeignKey) DrawIcon(context, "link", new Point(rect.X + PaddingX, y + 4), 12, secondary);
                    else context.DrawEllipse(muted, null, new Point(rect.X + PaddingX + 6, y + RowHeight / 2), 1.8, 1.8);
                    var name = Text(column.Name, 12, column.IsPrimaryKey ? _bold : _typeface, column.IsPrimaryKey ? primary : body);
                    name.MaxTextWidth = width - PaddingX * 2 - 22 - (_model.ShowTypes ? 70 : 0);
                    name.Trimming = TextTrimming.CharacterEllipsis;
                    name.MaxLineCount = 1;
                    context.DrawText(name, new Point(rect.X + PaddingX + 20, y + (RowHeight - name.Height) / 2));
                    if (_model.ShowTypes)
                    {
                        var type = Text(ShortType(column.Type), 11, _mono, muted);
                        context.DrawText(type, new Point(rect.Right - PaddingX - type.Width, y + (RowHeight - type.Height) / 2));
                    }
                    y += RowHeight;
                }
                if (columns.Count == 0)
                {
                    var none = Text("(nessuna chiave)", 11, _typeface, muted);
                    context.DrawText(none, new Point(rect.X + PaddingX, y + 3));
                }
            }
        }
    }

    private void DrawIcon(DrawingContext context, string kind, Point origin, double size, IBrush brush)
    {
        var geometry = NfIcons.Get(kind);
        if (geometry is null) return;
        var scale = size / 24;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(origin.X, origin.Y)))
            context.DrawGeometry(null, new Pen(brush, 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
    }

    private double RowCenter(DiagramTable table, string column)
    {
        var columns = _model!.VisibleColumns(table);
        var index = columns.ToList().FindIndex(c => c.Name == column);
        return index < 0 ? table.Y + HeaderHeight / 2 : table.Y + HeaderHeight + 3 + index * RowHeight + RowHeight / 2;
    }

    /// <summary>Punti della relazione: attacco, inizio curva, due punti di controllo, fine curva, attacco di arrivo.</summary>
    private (Point Start, Point CurveStart, Point Control1, Point Control2, Point CurveEnd, Point End) RelationPath(DiagramRelation relation)
    {
        var (fromWidth, _) = Measure(relation.From);
        var (toWidth, _) = Measure(relation.To);
        var y1 = RowCenter(relation.From, relation.FromColumn);
        var y2 = RowCenter(relation.To, relation.ToColumn);
        double x1, x2, d1, d2;
        if (relation.From == relation.To)
        {
            x1 = x2 = relation.From.X + fromWidth;
            d1 = d2 = 1;
        }
        else if (relation.To.X >= relation.From.X + fromWidth + 20) { x1 = relation.From.X + fromWidth; x2 = relation.To.X; d1 = 1; d2 = -1; }
        else if (relation.To.X + toWidth + 20 <= relation.From.X) { x1 = relation.From.X; x2 = relation.To.X + toWidth; d1 = -1; d2 = 1; }
        else { x1 = relation.From.X + fromWidth; x2 = relation.To.X + toWidth; d1 = 1; d2 = 1; }
        var start = new Point(x1 + d1 * 12, y1);
        var end = new Point(x2 + d2 * 12, y2);
        var bend = relation.From == relation.To ? 50 : Math.Max(40, Math.Abs(end.X - start.X) / 2);
        return (new Point(x1, y1), start, new Point(start.X + d1 * bend, start.Y), new Point(end.X + d2 * bend, end.Y), end, new Point(x2, y2));
    }

    private void DrawRelation(DrawingContext context, DiagramRelation relation, IPen pen)
    {
        var (p0, p1, c1, c2, p2, p3) = RelationPath(relation);
        var path = new StreamGeometry();
        using (var g = path.Open())
        {
            g.BeginFigure(p0, false);
            g.LineTo(p1);
            g.CubicBezierTo(c1, c2, p2);
            g.LineTo(p3);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, path);
        var d1 = Math.Sign(p1.X - p0.X);
        var d2 = Math.Sign(p2.X - p3.X);
        // lato «molti» (FK): zampa di gallina
        context.DrawLine(pen, new Point(p0.X + d1 * 10, p0.Y), new Point(p0.X, p0.Y - 5));
        context.DrawLine(pen, new Point(p0.X + d1 * 10, p0.Y), new Point(p0.X, p0.Y + 5));
        // lato «uno» (PK): doppia barra
        context.DrawLine(pen, new Point(p3.X + d2 * 5, p3.Y - 5), new Point(p3.X + d2 * 5, p3.Y + 5));
        context.DrawLine(pen, new Point(p3.X + d2 * 9, p3.Y - 5), new Point(p3.X + d2 * 9, p3.Y + 5));
    }

    // ───────────── Interazione ─────────────

    private DiagramTable? HitTest(Point screen)
    {
        if (_model is null) return null;
        var world = (screen - _offset) / _zoom;
        return _model.Tables.LastOrDefault(t =>
        {
            var (w, h) = Measure(t);
            return world.X >= t.X && world.X <= t.X + w && world.Y >= t.Y && world.Y <= t.Y + h;
        });
    }

    /// <summary>Colonna sotto il puntatore (null sull'intestazione).</summary>
    private (DiagramTable Table, string? Column)? HitColumn(Point screen)
    {
        if (HitTest(screen) is not { } table) return null;
        var world = (screen - _offset) / _zoom;
        var index = (int)Math.Floor((world.Y - table.Y - HeaderHeight - 3) / RowHeight);
        var columns = _model!.VisibleColumns(table);
        return (table, index >= 0 && index < columns.Count ? columns[index].Name : null);
    }

    /// <summary>Relazione vicina al puntatore (campionando la curva come in <see cref="DrawRelation"/>).</summary>
    private DiagramRelation? HitRelation(Point screen)
    {
        if (_model is null) return null;
        var world = (screen - _offset) / _zoom;
        foreach (var relation in _model.Relations)
        {
            var (p0, p1, c1, c2, p2, p3) = RelationPath(relation);
            if (Near(p0, p1) || Near(p2, p3)) return relation;
            for (var i = 0; i <= 40; i++)
            {
                var t = i / 40d;
                var u = 1 - t;
                var point = new Point(u * u * u * p1.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * p2.X,
                                      u * u * u * p1.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * p2.Y);
                if (Distance(point, world) < 6 / _zoom) return relation;
            }
            bool Near(Point a, Point b) => Distance(new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), world) < 6 / _zoom;
        }
        return null;
        static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    private void ShowMenu(Point screen)
    {
        if (_model is not { } model) return;
        var items = new List<MenuItem>();
        MenuItem Item(string header, string icon, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, Icon = new NfIcon { Kind = icon, Width = 14, Height = 14, Classes = { "menu-icon" } } };
            item.Click += (_, _) => action();
            return item;
        }
        if (HitRelation(screen) is { } relation && HitTest(screen) is null)
        {
            items.Add(Item($"Elimina relazione «{relation.Name}»", "trash", () => _ = model.DeleteRelationAsync(relation)));
            items.Add(Item($"Vai a {relation.From.Name}", "table", () => { model.SelectedTable = relation.From; CenterOn(relation.From); }));
            items.Add(Item($"Vai a {relation.To.Name}", "table", () => { model.SelectedTable = relation.To; CenterOn(relation.To); }));
        }
        else if (HitColumn(screen) is { } hit)
        {
            model.SelectedTable = hit.Table;
            items.Add(Item("Apri dati", "table-grid", () => _ = model.OpenTableAsync(hit.Table)));
            items.Add(Item("Progetta tabella", "pencil", () => model.Design(hit.Table), !hit.Table.IsView));
            if (hit.Column is { } column && !hit.Table.IsView)
                items.Add(Item($"Nuova relazione da «{column}»…", "link", () => { _link = (hit.Table, column); _linkPoint = (screen - _offset) / _zoom; _linkTarget = null; model.StatusMessage = "Clicca la tabella (o la colonna) di destinazione · Esc per annullare"; InvalidateVisual(); }));
            foreach (var r in model.Relations.Where(r => r.From == hit.Table).GroupBy(r => r.Name).Select(g => g.First()))
                items.Add(Item($"Elimina relazione «{r.Name}» → {r.To.Name}", "trash", () => _ = model.DeleteRelationAsync(r)));
        }
        else
        {
            items.Add(Item("Nuova tabella…", "plus", () => model.NewTableCommand.Execute(null)));
            items.Add(Item("Genera script SQL del modello", "file-code", () => model.GenerateSqlCommand.Execute(null)));
            items.Add(Item("Riorganizza", "network", Relayout));
            items.Add(Item("Adatta alla finestra", "maximize", Fit));
        }
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(this);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            ShowMenu(point);
            e.Handled = true;
            return;
        }
        // Relazione avviata dal menu: il clic sceglie la destinazione.
        if (_link is { } pending)
        {
            var target = HitColumn(point);
            _link = null;
            _linkTarget = null;
            InvalidateVisual();
            if (target is { } t && _model is not null) _ = _model.CreateRelationAsync(pending.Table, pending.Column, t.Table, t.Column);
            return;
        }
        var hit = HitTest(point);
        if (_model is not null) _model.SelectedTable = hit;
        // Strumento «Nuova relazione» o Ctrl+trascina da una colonna.
        if (hit is not null && !hit.IsView && (_model!.IsLinkMode || e.KeyModifiers.HasFlag(KeyModifiers.Control)) && HitColumn(point) is { Column: { } column })
        {
            _link = (hit, column);
            _linkPoint = (point - _offset) / _zoom;
            _linkTarget = null;
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }
        if (hit is not null && e.ClickCount == 2)
        {
            _ = _model!.OpenTableAsync(hit);
            return;
        }
        if (hit is not null)
        {
            _dragTable = hit;
            _dragStart = point;
            _dragOrigin = new Point(hit.X, hit.Y);
        }
        else
        {
            _panning = true;
            _dragStart = point;
            _panOrigin = _offset;
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_link is not null)
        {
            _linkPoint = (point - _offset) / _zoom;
            var target = HitColumn(point);
            _linkTarget = target is { } t && t.Table != _link.Value.Table && !t.Table.IsView ? t : null;
            Cursor = new Cursor(_linkTarget is null ? StandardCursorType.Cross : StandardCursorType.Hand);
            InvalidateVisual();
        }
        else if (_dragTable is not null)
        {
            _moved = true;
            var delta = (point - _dragStart) / _zoom;
            _dragTable.X = _dragOrigin.X + delta.X;
            _dragTable.Y = _dragOrigin.Y + delta.Y;
            InvalidateVisual();
        }
        else if (_panning)
        {
            _offset = _panOrigin + (point - _dragStart);
            InvalidateVisual();
        }
        else Cursor = HitTest(point) is not null
            ? new Cursor(_model?.IsLinkMode == true || e.KeyModifiers.HasFlag(KeyModifiers.Control) ? StandardCursorType.Cross : StandardCursorType.Hand)
            : HitRelation(point) is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_link is { } link && e.InitialPressMouseButton == MouseButton.Left && _linkTarget is { } target)
        {
            _link = null;
            _linkTarget = null;
            InvalidateVisual();
            _ = _model?.CreateRelationAsync(link.Table, link.Column, target.Table, target.Column);
        }
        else if (_link is not null && _linkTarget is null && HitColumn(e.GetPosition(this))?.Table == _link.Value.Table)
        {
            // Rilasciato sulla stessa tabella: si resta in attesa del clic sulla destinazione.
            if (_model is not null) _model.StatusMessage = "Clicca la tabella (o la colonna) di destinazione · Esc per annullare";
        }
        else if (_link is not null)
        {
            _link = null;
            _linkTarget = null;
            InvalidateVisual();
        }
        if (_dragTable is not null && _moved) _model?.SaveLayout();
        _moved = false;
        _dragTable = null;
        _panning = false;
        Cursor = Cursor.Default;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _offset += new Vector(e.Delta.Y * 60, 0);
            InvalidateVisual();
        }
        else ZoomAround(e.GetPosition(this), e.Delta.Y > 0 ? 1.12 : 1 / 1.12);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus: ZoomIn(); e.Handled = true; break;
            case Key.Subtract or Key.OemMinus: ZoomOut(); e.Handled = true; break;
            case Key.D0 or Key.NumPad0: Fit(); e.Handled = true; break;
            case Key.Escape when _link is not null:
                _link = null;
                _linkTarget = null;
                if (_model is not null) _model.StatusMessage = "Nuova relazione annullata.";
                InvalidateVisual();
                e.Handled = true;
                break;
        }
    }
}
