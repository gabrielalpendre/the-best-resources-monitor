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
        public static readonly Color Surface = C(0xfcfcfb), Page = C(0xf3f3f0), Ink = C(0x0b0b0b), Ink2 = C(0x52514e), Muted = C(0x898781),
            Grid = C(0xe1e0d9), Axis = C(0xc3c2b7), Series = C(0x2a78d6), Good = C(0x0ca30c), Warn = C(0xfab219), WarnText = C(0x9a6700),
            Crit = C(0xd03b3b), CritRow = C(0xfbe4e3), WarnRow = C(0xfef3d9), Track = C(0xecebe6), BarOk = C(0x86b6ef), Select = C(0xcde2fb),
            AccentBg = C(0xeef5fd), Agent = C(0x6250d6);

        public static readonly IBrush BSurface = new SolidColorBrush(Surface), BPage = new SolidColorBrush(Page), BInk = new SolidColorBrush(Ink),
            BInk2 = new SolidColorBrush(Ink2), BMuted = new SolidColorBrush(Muted), BGrid = new SolidColorBrush(Grid), BAxis = new SolidColorBrush(Axis),
            BSeries = new SolidColorBrush(Series), BCrit = new SolidColorBrush(Crit), BWarnText = new SolidColorBrush(WarnText), BAgent = new SolidColorBrush(Agent),
            BCritRow = new SolidColorBrush(CritRow), BWarnRow = new SolidColorBrush(WarnRow), BTrack = new SolidColorBrush(Track), BSelect = new SolidColorBrush(Select),
            BAccentBg = new SolidColorBrush(AccentBg);

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
