// Mini monitor sobreposto: janela pequena, sem borda, sempre por cima dos outros apps e arrastavel.
// A posicao fica salva em overlay.pos para reabrir no mesmo lugar.
// No Linux (X11/XWayland) a janela e "override-redirect", como tooltips e overlays de jogos: o gerenciador de janelas
// nao a controla e ela fica na camada mais alta, acima ate de apps em tela cheia (o "sempre no topo" comum fica abaixo
// da tela cheia ativa no KWin). Por isso o arraste e feito aqui, movendo a janela a mao.
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace EvidenceMonitor
{
    class OverlayWindow : Window
    {
        static readonly string PosFile = Path.Combine(Paths.Root, "overlay.pos");
        readonly Dictionary<string, TextBlock> values = new Dictionary<string, TextBlock>();
        readonly Dictionary<string, Control> rows = new Dictionary<string, Control>();
        readonly SolidColorBrush bg = new SolidColorBrush();
        bool dragging;
        Point grab;
        PixelPoint cur; // posicao pedida durante o arraste (Position so atualiza quando o X confirma)

        public Action OpenPanelRequested, CloseRequested;

        public OverlayWindow()
        {
            Title = "Mini monitor";
            SystemDecorations = SystemDecorations.None;
            Topmost = true; ShowInTaskbar = false; CanResize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Background = Brushes.Transparent;
            FontFamily = Pal.Font; FontSize = 12;

            var stack = new StackPanel { Spacing = 2 };
            foreach (var k in new[] { "CPU", "RAM", "GPU", "Disco", "Rede" }) stack.Children.Add(Row(k));
            var card = new Border
            {
                Child = stack, Background = bg, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7),
                BorderBrush = Pal.BAxis, BorderThickness = new Thickness(1), MinWidth = 170
            };
            Content = card;
            ToolTip.SetTip(card, "Arraste para mover · botão direito para opções · duplo clique abre o painel");

            // arraste manual: a janela anda junto com o ponteiro, mantendo o ponto onde foi agarrada
            card.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.ClickCount != 1) return;
                dragging = true; grab = e.GetPosition(this); cur = Position; e.Pointer.Capture(card);
            };
            card.PointerMoved += (s, e) =>
            {
                if (!dragging) return;
                var d = e.GetPosition(this) - grab;
                if (d.X == 0 && d.Y == 0) return;
                cur = new PixelPoint(cur.X + (int)Math.Round(d.X * RenderScaling), cur.Y + (int)Math.Round(d.Y * RenderScaling));
                Position = cur;
            };
            card.PointerReleased += (s, e) => { if (!dragging) return; dragging = false; e.Pointer.Capture(null); SavePos(); };
            card.PointerCaptureLost += (s, e) => dragging = false;
            card.DoubleTapped += (s, e) => OpenPanelRequested?.Invoke();
            var ctx = new ContextMenu();
            ctx.Items.Add(Item("Abrir painel", () => OpenPanelRequested?.Invoke()));
            ctx.Items.Add(new Separator());
            ctx.Items.Add(Item("Fechar mini monitor", () => CloseRequested?.Invoke()));
            card.ContextMenu = ctx;

            Opened += (s, e) => { ApplyTheme(); RestorePos(); };
            // a janela X11 ja existe aqui, mas ainda nao foi mapeada: e o momento de marcar override-redirect
            if (OperatingSystem.IsLinux()) X11Above.Apply(TryGetPlatformHandle());
        }

        static MenuItem Item(string h, Action a) { var m = new MenuItem { Header = h }; m.Click += (s, e) => a(); return m; }

        Control Row(string label)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("46,*") };
            g.Children.Add(new TextBlock { Text = label, Foreground = Pal.BMuted, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            var v = new TextBlock { Text = "–", Foreground = Pal.BInk, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(v, 1); g.Children.Add(v);
            values[label] = v; rows[label] = g;
            return g;
        }

        // fundo levemente translucido, na cor do tema
        public void ApplyTheme()
        {
            var c = Pal.Surface;
            bg.Color = Color.FromArgb(0xe8, c.R, c.G, c.B);
        }

        void Set(string k, string text, int lv)
        {
            values[k].Text = text;
            values[k].Foreground = Pal.Text(lv);
        }

        public void UpdateData(Dictionary<string, object> r)
        {
            ApplyTheme();
            double cpu = Fmt.D(r, "cpu"), avail = Fmt.D(r, "mem_avail"), ram = Fmt.D(r, "ram_gb"), commit = Fmt.D(r, "commit");
            Set("CPU", cpu >= 0 ? Fmt.N(cpu, 0) + "%" + (Fmt.D(r, "freq") > 0 ? " · " + Fmt.N(Fmt.D(r, "freq") / 1000, 1) + " GHz" : "") : "–", cpu >= 90 ? 2 : cpu >= 70 ? 1 : 0);

            int mlv = commit >= 90 || (avail >= 0 && avail < 500) ? 2 : commit >= 80 || (avail >= 0 && avail < 1500) ? 1 : 0;
            string mem = avail < 0 ? "–" : ram > 0
                ? Fmt.N(Math.Max(0, ram - avail / 1024), 1) + " / " + Fmt.N(ram, 0) + " GB"
                : Fmt.N(avail / 1024, 1) + " GB livre";
            Set("RAM", mem, mlv);

            double gpu = Fmt.D(r, "gpu");
            rows["GPU"].IsVisible = gpu >= 0;
            if (gpu >= 0) Set("GPU", Fmt.N(gpu, 0) + "%", gpu >= 95 ? 2 : gpu >= 80 ? 1 : 0);

            double busy = Fmt.D(r, "disk_busy"), mbs = Fmt.D(r, "disk_mbs");
            rows["Disco"].IsVisible = busy >= 0 || mbs >= 0;
            if (busy >= 0) Set("Disco", Fmt.N(busy, 0) + "%", busy >= 95 ? 2 : busy >= 70 ? 1 : 0);
            else if (mbs >= 0) Set("Disco", Fmt.N(mbs, 1) + " MB/s", 0);

            double rx = Fmt.D(r, "net_rx"), tx = Fmt.D(r, "net_tx");
            rows["Rede"].IsVisible = rx >= 0;
            if (rx >= 0) Set("Rede", "↓" + Fmt.N(rx, 1) + "  ↑" + Fmt.N(tx, 1) + " Mbit/s", 0);
        }

        void SavePos()
        {
            try { File.WriteAllText(PosFile, cur.X.ToString(CultureInfo.InvariantCulture) + " " + cur.Y.ToString(CultureInfo.InvariantCulture)); } catch { }
        }

        // posicao salva, se ainda cair dentro de alguma tela; senao canto superior direito da tela principal
        void RestorePos()
        {
            try
            {
                var p = File.Exists(PosFile) ? File.ReadAllText(PosFile).Split(' ') : null;
                if (p != null && p.Length == 2)
                {
                    var pos = new PixelPoint(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture));
                    foreach (var s in Screens.All)
                        if (s.WorkingArea.Contains(pos + new PixelPoint(20, 20))) { Position = pos; return; }
                }
                var scr = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
                if (scr != null)
                {
                    var card = (Control)Content; card.Measure(Size.Infinity);
                    int w = (int)(card.DesiredSize.Width * scr.Scaling);
                    Position = new PixelPoint(scr.WorkingArea.Right - w - 20, scr.WorkingArea.Y + 20);
                }
            }
            catch { }
        }
    }

    // Xlib direto: o Avalonia nao expoe override-redirect para janelas comuns
    static class X11Above
    {
        [StructLayout(LayoutKind.Sequential)]
        struct XSetWindowAttributes
        {
            public IntPtr background_pixmap, background_pixel, border_pixmap, border_pixel;
            public int bit_gravity, win_gravity, backing_store;
            public IntPtr backing_planes, backing_pixel;
            public int save_under;
            public IntPtr event_mask, do_not_propagate_mask;
            public int override_redirect;
            public IntPtr colormap, cursor;
        }
        const ulong CWOverrideRedirect = 1 << 9;

        [DllImport("libX11.so.6")] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport("libX11.so.6")] static extern int XCloseDisplay(IntPtr dpy);
        [DllImport("libX11.so.6")] static extern int XChangeWindowAttributes(IntPtr dpy, IntPtr win, UIntPtr mask, ref XSetWindowAttributes attrs);
        [DllImport("libX11.so.6")] static extern int XSync(IntPtr dpy, bool discard);

        public static void Apply(Avalonia.Platform.IPlatformHandle h)
        {
            if (h == null || h.HandleDescriptor != "XID") return;
            try
            {
                IntPtr dpy = XOpenDisplay(IntPtr.Zero); if (dpy == IntPtr.Zero) return;
                var a = new XSetWindowAttributes { override_redirect = 1 };
                XChangeWindowAttributes(dpy, h.Handle, (UIntPtr)CWOverrideRedirect, ref a);
                XSync(dpy, false); XCloseDisplay(dpy);
            }
            catch { }
        }
    }
}
