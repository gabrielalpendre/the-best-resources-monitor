// Paleta e tipografia do painel (iguais nos tres sistemas: a fonte Inter vai embutida no executavel).
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace EvidenceMonitor
{
    static class Pal
    {
        static Color C(uint rgb) { return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); }

        // Cores atuais (mudam com o tema do sistema; os graficos leem a cada desenho)
        public static Color Surface, Page, Ink, Ink2, Muted, Grid, Axis, Series, Good, Warn, WarnText, Crit, CritRow, WarnRow, Track, BarOk, Select, AccentBg, Agent;
        public static bool Dark { get; private set; }

        // Pinceis: sempre as mesmas instancias, so a cor muda -> quem ja usa o pincel acompanha a troca de tema
        public static readonly SolidColorBrush BSurface = new SolidColorBrush(), BPage = new SolidColorBrush(), BInk = new SolidColorBrush(),
            BInk2 = new SolidColorBrush(), BMuted = new SolidColorBrush(), BGrid = new SolidColorBrush(), BAxis = new SolidColorBrush(),
            BSeries = new SolidColorBrush(), BCrit = new SolidColorBrush(), BWarnText = new SolidColorBrush(), BAgent = new SolidColorBrush(),
            BCritRow = new SolidColorBrush(), BWarnRow = new SolidColorBrush(), BTrack = new SolidColorBrush(), BSelect = new SolidColorBrush(),
            BAccentBg = new SolidColorBrush();

        static Pal() { Apply(false); }

        public static void Apply(bool dark)
        {
            Dark = dark;
            if (!dark)
            {
                Surface = C(0xfcfcfb); Page = C(0xf3f3f0); Ink = C(0x0b0b0b); Ink2 = C(0x52514e); Muted = C(0x898781);
                Grid = C(0xe1e0d9); Axis = C(0xc3c2b7); Series = C(0x2a78d6); Good = C(0x0ca30c); Warn = C(0xfab219); WarnText = C(0x9a6700);
                Crit = C(0xd03b3b); CritRow = C(0xfbe4e3); WarnRow = C(0xfef3d9); Track = C(0xecebe6); BarOk = C(0x86b6ef); Select = C(0xcde2fb);
                AccentBg = C(0xeef5fd); Agent = C(0x6250d6);
            }
            else
            {
                Surface = C(0x1e1e1c); Page = C(0x141413); Ink = C(0xededea); Ink2 = C(0xb8b7b0); Muted = C(0x8c8b84);
                Grid = C(0x34332f); Axis = C(0x4d4c46); Series = C(0x4b96ee); Good = C(0x3cc43c); Warn = C(0xfab219); WarnText = C(0xf2bd4e);
                Crit = C(0xef6262); CritRow = C(0x3f2222); WarnRow = C(0x3b311a); Track = C(0x2f2e2b); BarOk = C(0x3d6ea9); Select = C(0x1d3a5e);
                AccentBg = C(0x192a3e); Agent = C(0x9d8ff2);
            }
            BSurface.Color = Surface; BPage.Color = Page; BInk.Color = Ink; BInk2.Color = Ink2; BMuted.Color = Muted; BGrid.Color = Grid;
            BAxis.Color = Axis; BSeries.Color = Series; BCrit.Color = Crit; BWarnText.Color = WarnText; BAgent.Color = Agent; BCritRow.Color = CritRow;
            BWarnRow.Color = WarnRow; BTrack.Color = Track; BSelect.Color = Select; BAccentBg.Color = AccentBg;
        }

        public static IBrush Text(int lv) { return lv == 2 ? BCrit : lv == 1 ? BWarnText : BInk; }
        public static Color Bar(int lv) { return lv == 2 ? Crit : lv == 1 ? Warn : BarOk; }

        public static readonly FontFamily Font = new FontFamily("fonts:Inter#Inter, $Default");
        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, Menlo, SF Mono, DejaVu Sans Mono, Liberation Mono, monospace");
        public static readonly Typeface Regular = new Typeface(Font), Semi = new Typeface(Font, FontStyle.Normal, FontWeight.SemiBold), Bold = new Typeface(Font, FontStyle.Normal, FontWeight.Bold);

        public static FormattedText Txt(string s, Typeface tf, double size, IBrush brush)
        {
            return new FormattedText(s ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size, brush);
        }

        // Texto de uma linha cortado com "..." na largura dada
        public static FormattedText Fit(string s, Typeface tf, double size, IBrush brush, double maxWidth)
        {
            var t = Txt(s, tf, size, brush);
            t.MaxTextWidth = System.Math.Max(1, maxWidth); t.MaxLineCount = 1; t.Trimming = TextTrimming.CharacterEllipsis;
            return t;
        }

        public static Button Btn(string text, System.Action a, IBrush fore = null, bool primary = false)
        {
            var b = new Button
            {
                Content = text, Padding = new Thickness(12, 5), Margin = new Thickness(3, 0), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
                Background = primary ? BSeries : BSurface, BorderBrush = BAxis, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4)
            };
            if (fore != null) b.Foreground = fore; else if (primary) b.Foreground = Brushes.White;
            b.Click += (s, e) => a();
            return b;
        }

        public static TextBlock Label(string text, IBrush fore = null, double size = 12.5)
        {
            return new TextBlock { Text = text, Foreground = fore ?? BInk2, FontSize = size, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        }
    }
}
