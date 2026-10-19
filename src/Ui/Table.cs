// Tabela desenhada a mao (igual nos tres sistemas): cabecalho clicavel, selecao, rolagem, barras de consumo,
// cores por linha, duplo-clique/Enter e Delete.
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace EvidenceMonitor
{
    class TableColumn
    {
        public string Header; public double Width; public bool Right;
        public TableColumn(string h, double w, bool right = false) { Header = h; Width = w; Right = right; }
    }

    // Conteudo de uma celula
    class Cell
    {
        public string Text; public IBrush Fg; public bool Bold;
        public double BarPct = -1; public Color BarColor;   // barra fina embaixo do texto (0-100)
        public Color? Dot;                                 // bolinha colorida antes do texto
        public static Cell Of(string t, IBrush fg = null, bool bold = false) { return new Cell { Text = t, Fg = fg, Bold = bold }; }
    }

    class Table : Control
    {
        public readonly List<TableColumn> Columns = new List<TableColumn>();
        public Func<object, int, Cell> CellOf = (r, c) => Cell.Of("");
        public Func<object, IBrush> RowBg = r => null;
        public Func<object, object, bool> SameRow = (a, b) => Equals(a, b);
        public double RowH = 26, HeadH = 28;
        public event Action SelectionChanged, Activated, DeleteKey;
        public event Action<int> HeaderClicked;

        List<object> rows = new List<object>();
        object selected;
        double scroll;
        bool dragThumb; double dragOffset;

        public object Selected { get { return selected; } }
        public int Count { get { return rows.Count; } }

        public Table() { Focusable = true; ClipToBounds = true; }

        public void SetRows(IEnumerable<object> items)
        {
            rows = items.ToList();
            if (selected != null) selected = rows.FirstOrDefault(r => SameRow(r, selected));
            ClampScroll(); InvalidateVisual();
        }

        public void Select(object row, bool notify = true)
        {
            selected = row; InvalidateVisual();
            if (notify) SelectionChanged?.Invoke();
        }

        double ViewH { get { return Math.Max(0, Bounds.Height - HeadH); } }
        double ContentH { get { return rows.Count * RowH; } }
        void ClampScroll() { scroll = Math.Max(0, Math.Min(scroll, ContentH - ViewH)); }

        double[] ColX(out double total)
        {
            // a ultima coluna ocupa o resto da largura
            var xs = new double[Columns.Count + 1]; double x = 0;
            for (int i = 0; i < Columns.Count; i++) { xs[i] = x; x += Columns[i].Width; }
            double avail = Bounds.Width - 10;
            if (Columns.Count > 0 && x < avail) x = avail;
            xs[Columns.Count] = x; total = x;
            return xs;
        }

        Rect Thumb()
        {
            double vh = ViewH, ch = ContentH;
            if (ch <= vh || vh <= 0) return default;
            double h = Math.Max(24, vh * vh / ch), y = HeadH + (vh - h) * scroll / (ch - vh);
            return new Rect(Bounds.Width - 9, y, 7, h);
        }

        public override void Render(DrawingContext g)
        {
            double W = Bounds.Width, H = Bounds.Height;
            g.DrawRectangle(Pal.BSurface, null, new Rect(0, 0, W, H));
            double total; var xs = ColX(out total);

            int first = (int)(scroll / RowH), last = Math.Min(rows.Count - 1, (int)((scroll + ViewH) / RowH) + 1);
            using (g.PushClip(new Rect(0, HeadH, W, ViewH)))
                for (int i = first; i <= last && i >= 0; i++)
                {
                    var r = rows[i]; double y = HeadH + i * RowH - scroll;
                    bool sel = selected != null && SameRow(r, selected);
                    var bg = sel ? Pal.BSelect : RowBg(r);
                    if (bg != null) g.DrawRectangle(bg, null, new Rect(0, y, W, RowH));
                    if (sel) g.DrawRectangle(Pal.BSeries, null, new Rect(0, y, 3, RowH));
                    for (int c = 0; c < Columns.Count; c++)
                    {
                        var cell = CellOf(r, c); if (cell == null) continue;
                        double cx = xs[c] + 7, cw = xs[c + 1] - xs[c] - 14;
                        if (cw <= 4) continue;
                        if (cell.Dot.HasValue) { g.DrawEllipse(new SolidColorBrush(cell.Dot.Value), null, new Point(cx + 5, y + RowH / 2), 5, 5); cx += 16; cw -= 16; }
                        bool bar = cell.BarPct >= 0;
                        var t = Pal.Fit(cell.Text ?? "", cell.Bold ? Pal.Bold : Pal.Regular, 12.5, cell.Fg ?? Pal.BInk2, cw);
                        if (Columns[c].Right) t.TextAlignment = TextAlignment.Right;
                        double ty = bar ? y + (RowH - 5 - t.Height) / 2 : y + (RowH - t.Height) / 2;
                        g.DrawText(t, new Point(cx, ty));
                        if (bar)
                        {
                            var br = new Rect(cx, y + RowH - 6, cw, 3);
                            g.DrawRectangle(Pal.BTrack, null, br);
                            double w = br.Width * Math.Min(100, cell.BarPct) / 100; if (cell.BarPct > 0 && w < 2) w = 2;
                            g.DrawRectangle(new SolidColorBrush(cell.BarColor), null, new Rect(br.X, br.Y, w, br.Height));
                        }
                    }
                }

            // cabecalho
            g.DrawRectangle(Pal.BPage, null, new Rect(0, 0, W, HeadH));
            g.DrawLine(new Pen(Pal.BGrid, 1), new Point(0, HeadH - 0.5), new Point(W, HeadH - 0.5));
            for (int c = 0; c < Columns.Count; c++)
            {
                double cw = xs[c + 1] - xs[c] - 14; if (cw <= 4) continue;
                var t = Pal.Fit(Columns[c].Header, Pal.Semi, 12, Pal.BInk2, cw);
                if (Columns[c].Right) t.TextAlignment = TextAlignment.Right;
                g.DrawText(t, new Point(xs[c] + 7, (HeadH - t.Height) / 2));
                if (c > 0) g.DrawLine(new Pen(Pal.BGrid, 1), new Point(xs[c] + 0.5, 6), new Point(xs[c] + 0.5, HeadH - 6));
            }

            var th = Thumb();
            if (th.Height > 0) g.DrawRectangle(new SolidColorBrush(Pal.Axis), null, th, 3.5, 3.5);
        }

        int RowAt(Point p) { if (p.Y < HeadH) return -1; int i = (int)((p.Y - HeadH + scroll) / RowH); return i < rows.Count ? i : -1; }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var p = e.GetPosition(this);
            var th = Thumb();
            if (th.Height > 0 && p.X >= Bounds.Width - 12 && p.Y >= HeadH)
            {
                if (p.Y >= th.Y && p.Y <= th.Bottom) { dragThumb = true; dragOffset = p.Y - th.Y; e.Pointer.Capture(this); }
                else { scroll += (p.Y < th.Y ? -1 : 1) * ViewH; ClampScroll(); InvalidateVisual(); }
                return;
            }
            if (p.Y < HeadH)
            {
                double total; var xs = ColX(out total);
                for (int c = 0; c < Columns.Count; c++) if (p.X >= xs[c] && p.X < xs[c + 1]) { HeaderClicked?.Invoke(c); break; }
                return;
            }
            int i = RowAt(p);
            if (i >= 0)
            {
                Select(rows[i]);
                if (e.ClickCount == 2 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) Activated?.Invoke();
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!dragThumb) return;
            double vh = ViewH, ch = ContentH, th = Thumb().Height;
            double y = e.GetPosition(this).Y - dragOffset - HeadH;
            scroll = (ch - vh) * y / Math.Max(1, vh - th); ClampScroll(); InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); if (dragThumb) { dragThumb = false; e.Pointer.Capture(null); } }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            scroll -= e.Delta.Y * RowH * 3; ClampScroll(); InvalidateVisual(); e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int i = selected == null ? -1 : rows.FindIndex(r => SameRow(r, selected));
            int n = i;
            if (e.Key == Key.Down) n = Math.Min(rows.Count - 1, i + 1);
            else if (e.Key == Key.Up) n = Math.Max(0, i - 1);
            else if (e.Key == Key.PageDown) n = Math.Min(rows.Count - 1, i + (int)(ViewH / RowH));
            else if (e.Key == Key.PageUp) n = Math.Max(0, i - (int)(ViewH / RowH));
            else if (e.Key == Key.Enter) { Activated?.Invoke(); e.Handled = true; return; }
            else if (e.Key == Key.Delete || (e.Key == Key.Back && e.KeyModifiers.HasFlag(KeyModifiers.Meta))) { DeleteKey?.Invoke(); e.Handled = true; return; }
            else return;
            if (n >= 0 && n < rows.Count)
            {
                Select(rows[n]);
                double y = n * RowH;
                if (y < scroll) scroll = y; else if (y + RowH > scroll + ViewH) scroll = y + RowH - ViewH;
                ClampScroll(); InvalidateVisual();
            }
            e.Handled = true;
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); ClampScroll(); }
    }
}
