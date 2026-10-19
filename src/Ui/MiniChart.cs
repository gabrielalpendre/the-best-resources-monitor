// Grafico de linha do painel. O mouse sobre um grafico marca o mesmo instante em todos (HoverSync),
// cada um mostrando o proprio valor naquele momento.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace EvidenceMonitor
{
    // Cursor compartilhado entre os graficos do painel
    class HoverSync
    {
        public DateTime? T;
        public MiniChart Source;
        public event Action Changed;
        public void Set(DateTime? t, MiniChart src) { if (T == t && Source == src) return; T = t; Source = src; Changed?.Invoke(); }
    }

    class MiniChart : Control
    {
        public string Title = "", Unit = "", Fmt = "N0", Unavailable = null;
        public double FixedMax = -1, Warn = double.NaN, Crit = double.NaN;
        public bool LowerIsWorse;
        // Accent: card ligado ao processo selecionado na lista (fundo azul, borda azul, legenda); Flash() pisca ao trocar
        public bool Accent; public string Caption = "";
        DateTime flashUntil = DateTime.MinValue;
        readonly DispatcherTimer flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        List<KeyValuePair<DateTime, double>> data = new List<KeyValuePair<DateTime, double>>();
        List<DateTime> marks = new List<DateTime>();
        DateTime from, to;
        Rect plotRect;
        Point[] cols = new Point[0]; double[] colVals = new double[0]; DateTime[] colTimes = new DateTime[0];
        string emptyText = "Sem dados no período";

        HoverSync sync;
        public HoverSync Sync
        {
            get { return sync; }
            set { if (sync != null) sync.Changed -= InvalidateVisual; sync = value; if (sync != null) sync.Changed += InvalidateVisual; }
        }

        public MiniChart()
        {
            ClipToBounds = true;
            flashTimer.Tick += (s, e) => { InvalidateVisual(); if (DateTime.Now > flashUntil) flashTimer.Stop(); };
            DetachedFromVisualTree += (s, e) => { flashTimer.Stop(); Sync = null; };
        }

        public void Flash() { flashUntil = DateTime.Now.AddMilliseconds(1500); flashTimer.Start(); InvalidateVisual(); }

        public void SetData(List<KeyValuePair<DateTime, double>> d, List<DateTime> m, DateTime f, DateTime t, string empty)
        {
            data = d; marks = m; from = f; to = t; if (empty != null) emptyText = empty;
            InvalidateVisual();
        }

        static double NiceMax(double v)
        {
            if (!(v > 0)) return 1;
            double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (var m in new[] { 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 }) if (m * p >= v) return m * p;
            return 10 * p;
        }

        string F(double v) { return v.ToString(Fmt, EvidenceMonitor.Fmt.Br); }

        static readonly IPen GridPen = new Pen(Pal.BGrid, 1), AxisPen = new Pen(Pal.BAxis, 1);

        public override void Render(DrawingContext g)
        {
            double W = Bounds.Width, H = Bounds.Height;
            bool flashing = Accent && DateTime.Now < flashUntil;
            var bg = Accent ? (flashing ? Pal.BSelect : Pal.BAccentBg) : Pal.BSurface;
            double bw = Accent ? (flashing ? 4 : 2) : 1;
            g.DrawRectangle(bg, new Pen(Accent ? Pal.BSeries : Pal.BGrid, bw), new Rect(bw / 2, bw / 2, W - bw, H - bw), 6, 6);

            double top = Accent ? 50 : 36;
            var plot = new Rect(44, top, Math.Max(0, W - 52), Math.Max(0, H - top - 20));
            plotRect = plot;
            if (Accent && Caption.Length > 0) g.DrawText(Pal.Fit(Caption, Pal.Semi, 10, Pal.BSeries, W - 80), new Point(8, 26));
            double titleRight = W - 8;
            if (plot.Width < 20 || plot.Height < 20) return;

            if (data.Count == 0)
            {
                g.DrawText(Pal.Fit(Title, Pal.Semi, 12.5, Pal.BInk, titleRight - 8), new Point(8, 7));
                var et = Pal.Txt(Unavailable ?? emptyText, Pal.Regular, 10.5, Pal.BMuted);
                et.MaxTextWidth = plot.Width; et.TextAlignment = TextAlignment.Center;
                g.DrawText(et, new Point(plot.X, plot.Y + (plot.Height - et.Height) / 2));
                return;
            }

            double cur = data[data.Count - 1].Value;
            int lv = Limits.Level(cur, Warn, Crit, LowerIsWorse);
            var curText = Pal.Txt(F(cur) + " " + Unit, Pal.Semi, 15, Pal.Text(lv));
            g.DrawText(curText, new Point(W - curText.Width - 8, 3));
            titleRight = W - curText.Width - 14;
            if (lv > 0)
            {
                var tag = Pal.Txt(lv == 2 ? "● crítico" : "● atenção", Pal.Regular, 10, Pal.Text(lv));
                g.DrawText(tag, new Point(W - tag.Width - 8, 3 + curText.Height - 3));
            }
            g.DrawText(Pal.Fit(Title, Pal.Semi, 12.5, Pal.BInk, Math.Max(10, titleRight - 8)), new Point(8, 7));

            double vmax = data.Max(kv => kv.Value);
            double max = FixedMax > 0 ? FixedMax : NiceMax(vmax * 1.1);
            Func<double, double> Y = v => plot.Bottom - Math.Max(0, Math.Min(v, max)) / max * plot.Height;
            double span = Math.Max(1, (to - from).TotalMilliseconds);
            Func<DateTime, double> X = t => plot.X + (t - from).TotalMilliseconds / span * plot.Width;

            for (int i = 0; i <= 4; i++)
            {
                double v = max * i / 4, y = Math.Round(Y(v)) + 0.5;
                g.DrawLine(i == 0 ? AxisPen : GridPen, new Point(plot.X, y), new Point(plot.Right, y));
                string lab = v >= 10000 ? (v / 1000).ToString("N0", EvidenceMonitor.Fmt.Br) + "k" : v.ToString(max < 1 ? "N2" : max < 10 ? "N1" : "N0", EvidenceMonitor.Fmt.Br);
                var lt = Pal.Txt(lab, Pal.Regular, 10, Pal.BMuted);
                g.DrawText(lt, new Point(plot.X - lt.Width - 4, y - lt.Height / 2));
            }

            if (!double.IsNaN(Crit) && Crit > 0 && Crit < max)
                g.DrawLine(new Pen(new SolidColorBrush(Pal.Crit, 0.55), 1, DashStyle.Dash), new Point(plot.X, Y(Crit)), new Point(plot.Right, Y(Crit)));

            var markPen = new Pen(new SolidColorBrush(Pal.Crit, 0.8), 1.5, DashStyle.Dash);
            foreach (var m in marks) if (m >= from && m <= to) g.DrawLine(markPen, new Point(X(m), plot.Y), new Point(X(m), plot.Bottom));

            // um ponto por coluna de pixel: pior valor do intervalo
            int ncol = Math.Max(1, (int)plot.Width);
            var bv = new double[ncol]; var bt = new DateTime[ncol]; var has = new bool[ncol];
            foreach (var kv in data)
            {
                int c = (int)(X(kv.Key) - plot.X); if (c < 0 || c >= ncol) continue;
                if (!has[c] || (LowerIsWorse ? kv.Value < bv[c] : kv.Value > bv[c])) { bv[c] = kv.Value; bt[c] = kv.Key; has[c] = true; }
            }
            var ptsL = new List<Point>(); var valsL = new List<double>(); var timesL = new List<DateTime>();
            for (int c = 0; c < ncol; c++) if (has[c]) { ptsL.Add(new Point(plot.X + c, Y(bv[c]))); valsL.Add(bv[c]); timesL.Add(bt[c]); }
            cols = ptsL.ToArray(); colVals = valsL.ToArray(); colTimes = timesL.ToArray();

            double gapMs = Math.Max(60000, span / ncol * 3);
            var line = new Pen(Pal.BSeries, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var fill = new SolidColorBrush(Pal.Series, 0.11);
            int start = 0;
            for (int i = 1; i <= cols.Length; i++)
            {
                if (i == cols.Length || (colTimes[i] - colTimes[i - 1]).TotalMilliseconds > gapMs)
                {
                    var seg = cols.Skip(start).Take(i - start).ToArray();
                    if (seg.Length == 1) g.DrawEllipse(Pal.BSeries, null, seg[0], 2, 2);
                    else if (seg.Length > 1)
                    {
                        var area = new StreamGeometry();
                        using (var ctx = area.Open())
                        {
                            ctx.BeginFigure(new Point(seg[0].X, plot.Bottom), true);
                            foreach (var p in seg) ctx.LineTo(p);
                            ctx.LineTo(new Point(seg[seg.Length - 1].X, plot.Bottom));
                            ctx.EndFigure(true);
                        }
                        g.DrawGeometry(fill, null, area);
                        var path = new StreamGeometry();
                        using (var ctx = path.Open())
                        {
                            ctx.BeginFigure(seg[0], false);
                            for (int k = 1; k < seg.Length; k++) ctx.LineTo(seg[k]);
                            ctx.EndFigure(false);
                        }
                        g.DrawGeometry(null, line, path);
                    }
                    start = i;
                }
            }

            string tf = (to - from).TotalHours > 6 ? "dd/MM HH:mm" : "HH:mm";
            for (int i = 0; i <= 2; i++)
            {
                var t = from.AddMilliseconds(span * i / 2);
                var lt = Pal.Txt(t.ToString(tf, CultureInfo.InvariantCulture), Pal.Regular, 10, Pal.BMuted);
                double x = i == 0 ? plot.X : i == 2 ? plot.Right - lt.Width : plot.X + (plot.Width - lt.Width) / 2;
                g.DrawText(lt, new Point(x, plot.Bottom + 3));
            }

            // cursor sincronizado: mesma hora em todos os graficos; o ponto e o valor sao os desta serie mais proximos
            DateTime? ht = sync != null ? sync.T : null;
            if (ht.HasValue && ht.Value >= from && ht.Value <= to)
            {
                double hx = X(ht.Value);
                bool src = sync.Source == this;
                g.DrawLine(src ? new Pen(Pal.BInk2, 1) : new Pen(Pal.BMuted, 1, DashStyle.Dot), new Point(hx, plot.Y), new Point(hx, plot.Bottom));
                int bi = Nearest(ht.Value);
                bool hit = bi >= 0 && Math.Abs((colTimes[bi] - ht.Value).TotalMilliseconds) <= gapMs;
                string tip;
                if (hit)
                {
                    var pt = cols[bi];
                    g.DrawEllipse(Pal.BSeries, new Pen(Pal.BSurface, 2), pt, 4, 4);
                    tip = colTimes[bi].ToString(src ? "dd/MM HH:mm:ss" : "HH:mm:ss", CultureInfo.InvariantCulture) + "   " + F(colVals[bi]) + " " + Unit;
                }
                else tip = ht.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "   sem dado";
                var tt = Pal.Txt(tip, Pal.Semi, 10.5, hit ? Pal.BInk : Pal.BMuted);
                double tx = hx + 8; if (tx + tt.Width + 6 > W) tx = hx - tt.Width - 8;
                tx = Math.Max(plot.X, tx);
                double ty = plot.Y + 2;
                g.DrawRectangle(new SolidColorBrush(Pal.Surface, 0.96), new Pen(src ? Pal.BSeries : Pal.BAxis, 1), new Rect(tx - 4, ty - 2, tt.Width + 8, tt.Height + 4), 3, 3);
                g.DrawText(tt, new Point(tx, ty));
            }
        }

        int Nearest(DateTime t)
        {
            int bi = -1; double bd = double.MaxValue;
            for (int i = 0; i < colTimes.Length; i++) { double d = Math.Abs((colTimes[i] - t).TotalMilliseconds); if (d < bd) { bd = d; bi = i; } }
            return bi;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (sync == null || plotRect.Width <= 0 || to <= from) return;
            var p = e.GetPosition(this);
            if (p.X < plotRect.X || p.X > plotRect.Right) { sync.Set(null, null); return; }
            DateTime t = from.AddMilliseconds((to - from).TotalMilliseconds * (p.X - plotRect.X) / plotRect.Width);
            // gruda no ponto mais proximo desta serie, para os outros graficos mostrarem o mesmo instante
            int bi = Nearest(t);
            if (bi >= 0 && Math.Abs(cols[bi].X - p.X) <= 12) t = colTimes[bi];
            sync.Set(t, this);
        }

        protected override void OnPointerExited(PointerEventArgs e) { if (sync != null && sync.Source == this) sync.Set(null, null); base.OnPointerExited(e); }
    }
}
