// Janelas auxiliares iguais nos tres sistemas: perguntas, avisos (toast), texto detalhado, relatorio gerado
// e o comando para rodar como administrador.
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EvidenceMonitor
{
    static class Dialog
    {
        public const string AppTitle = "Monitor de Evidências";

        static Window Base(string title, double w)
        {
            return new Window
            {
                Title = title, Width = w, SizeToContent = SizeToContent.Height, CanResize = false, Topmost = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Pal.BSurface, FontFamily = Pal.Font, FontSize = 13
            };
        }

        // Pergunta com botoes; devolve o indice do botao clicado (ou -1 se fechou). danger pinta o primeiro de vermelho.
        public static Task<int> Ask(string text, string[] buttons, int defaultIdx = 0, bool danger = false, Window owner = null, double width = 480)
        {
            var tcs = new TaskCompletionSource<int>();
            var w = Base(AppTitle, width);
            int result = -1;
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(0, 16, 0, 0) };
            for (int i = 0; i < buttons.Length; i++)
            {
                int k = i;
                var b = Pal.Btn(buttons[i], () => { result = k; w.Close(); }, danger && i == 0 ? Pal.BCrit : null, !danger && i == defaultIdx);
                b.IsDefault = i == defaultIdx;
                if (i == buttons.Length - 1 && buttons.Length > 1) b.IsCancel = true;
                bar.Children.Add(b);
            }
            w.Content = new StackPanel
            {
                Margin = new Thickness(20, 18, 20, 16),
                Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Pal.BInk, LineHeight = 19 }, bar }
            };
            w.Closed += (s, e) => tcs.TrySetResult(result);
            if (owner != null && owner.IsVisible) { w.WindowStartupLocation = WindowStartupLocation.CenterOwner; w.ShowDialog(owner); }
            else w.Show();
            w.Activate();
            return tcs.Task;
        }

        public static async Task<bool> Confirm(string text, Window owner = null, bool danger = true, string yes = "Sim", string no = "Não")
        {
            return await Ask(text, new[] { yes, no }, danger ? 1 : 0, danger, owner) == 0;
        }

        public static Task Info(string text, Window owner = null) { return Ask(text, new[] { "OK" }, 0, false, owner); }

        // Texto longo em fonte monoespacada (detalhe de evento, pico de CPU)
        public static void Text(string title, string text, Window owner, double w = 860, double h = 520)
        {
            var win = new Window { Title = title, Width = w, Height = h, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Pal.BSurface, FontFamily = Pal.Font };
            var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = Pal.Mono, FontSize = 12.5, BorderThickness = new Thickness(0), Background = Pal.BSurface };
            ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            win.Content = box;
            win.KeyDown += (s, e) => { if (e.Key == Key.Escape) win.Close(); };
            if (owner != null) win.ShowDialog(owner); else win.Show();
        }
    }

    // Aviso no canto da tela (no lugar do balao da bandeja do Windows), clicavel, some em 10 s
    static class Toast
    {
        static Window cur;

        public static void Show(string title, string text, bool error = false, Action onClick = null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { cur?.Close(); } catch { }
                var accent = error ? Pal.BCrit : Pal.BSeries;
                var w = new Window
                {
                    SystemDecorations = SystemDecorations.None, Topmost = true, ShowActivated = false, ShowInTaskbar = false, CanResize = false,
                    Width = 380, SizeToContent = SizeToContent.Height, Background = Pal.BSurface, FontFamily = Pal.Font, Cursor = new Cursor(StandardCursorType.Hand)
                };
                var body = new StackPanel { Margin = new Thickness(14, 10, 14, 12), Spacing = 3 };
                body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13.5, Foreground = Pal.BInk, TextWrapping = TextWrapping.Wrap });
                if (!string.IsNullOrWhiteSpace(text)) body.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = Pal.BInk2, TextWrapping = TextWrapping.Wrap });
                body.Children.Add(new TextBlock { Text = Dialog.AppTitle, FontSize = 10.5, Foreground = Pal.BMuted, Margin = new Thickness(0, 4, 0, 0) });
                w.Content = new Border { BorderBrush = accent, BorderThickness = new Thickness(4, 1, 1, 1), Child = body };
                w.PointerPressed += (s, e) => { w.Close(); onClick?.Invoke(); };
                w.Opened += (s, e) =>
                {
                    var scr = w.Screens.Primary ?? w.Screens.ScreenFromWindow(w);
                    if (scr == null) return;
                    var wa = scr.WorkingArea; double sc = scr.Scaling;
                    int pw = (int)(w.Bounds.Width * sc), ph = (int)(w.Bounds.Height * sc), m = (int)(16 * sc);
                    // macOS: canto superior direito (onde ficam as notificacoes); Windows/Linux: inferior direito
                    w.Position = OperatingSystem.IsMacOS() ? new PixelPoint(wa.Right - pw - m, wa.Y + m) : new PixelPoint(wa.Right - pw - m, wa.Bottom - ph - m);
                };
                cur = w;
                w.Show();
                DispatcherTimer.RunOnce(() => { try { if (cur == w) { w.Close(); cur = null; } } catch { } }, TimeSpan.FromSeconds(10));
            });
        }
    }

    // Aviso de relatorio gerado, com links para abrir a pasta (arquivo selecionado) ou o proprio relatorio
    static class ReportDone
    {
        public static Task Show(string file)
        {
            var tcs = new TaskCompletionSource<bool>();
            var w = new Window { Title = "Relatório gerado", Width = 480, SizeToContent = SizeToContent.Height, CanResize = false, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Pal.BSurface, FontFamily = Pal.Font, FontSize = 13 };
            Button Link(string t, Action a) { var b = new Button { Content = t, Foreground = Pal.BSeries, Background = Brushes.Transparent, Padding = new Thickness(0), Margin = new Thickness(0, 0, 18, 0), Cursor = new Cursor(StandardCursorType.Hand) }; b.Click += (s, e) => { try { a(); } catch { } w.Close(); }; return b; }
            w.Content = new StackPanel
            {
                Margin = new Thickness(20, 18, 20, 16), Spacing = 12,
                Children = {
                    new TextBlock { Text = "Relatório salvo em Downloads:\n" + System.IO.Path.GetFileName(file), TextWrapping = TextWrapping.Wrap, Foreground = Pal.BInk },
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { Link("Abrir pasta", () => Plat.I.Reveal(file)), Link("Abrir relatório", () => Plat.I.OpenFile(file)) } },
                    new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { Pal.Btn("Fechar", () => w.Close()) } } }
            };
            w.Closed += (s, e) => tcs.TrySetResult(true);
            w.Show(); w.Activate();
            return tcs.Task;
        }
    }

    // Encerrar falhou por permissao: mostra o comando para rodar num terminal de administrador.
    // O app nao executa nada elevado; so gera o texto e, se pedido, abre um terminal.
    class ElevatedCmdWindow : Window
    {
        public ElevatedCmdWindow(string name, string cmd, Action reboot)
        {
            var P = Plat.I; bool win = OperatingSystem.IsWindows();
            Title = "Sem permissão para encerrar " + ElevatedCommand.Safe(name);
            Width = 760; Height = 430; MinWidth = 520; MinHeight = 320; WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true;
            Background = Pal.BSurface; FontFamily = Pal.Font; FontSize = 13;

            var lbl = new TextBlock
            {
                Margin = new Thickness(14, 12, 14, 8), TextWrapping = TextWrapping.Wrap, Foreground = Pal.BInk,
                Text = "Seu usuário não tem permissão para encerrar " + ElevatedCommand.Safe(name) + " (roda como " + (win ? "SYSTEM" : "root") + " ou outro usuário).\n" +
                       (win ? "Rode o comando num PowerShell como administrador." : "Rode o comando num terminal (vai pedir sua senha de administrador).") +
                       " Se não resolver, use \"Reiniciar " + P.RebootWord + "\"."
            };
            var box = new TextBox { Text = cmd.Replace("\r\n", "\n").TrimEnd(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = Pal.Mono, FontSize = 12.5, Background = Pal.BPage, Margin = new Thickness(14, 0) };
            Button copy = null;
            Action doCopy = async () => { try { await Clipboard.SetTextAsync(cmd); copy.Content = "Copiado ✓"; } catch { } };
            copy = Pal.Btn("Copiar comando", () => doCopy());
            var term = Pal.Btn(P.AdminTerminalLabel, async () =>
            {
                doCopy();
                string err; if (!P.OpenAdminTerminal(out err) && err != null) await Dialog.Info(err, this);
            });
            var rb = Pal.Btn("Reiniciar " + P.RebootWord + " (último recurso)", () => reboot?.Invoke(), Pal.BCrit);
            var close = Pal.Btn("Fechar", Close); close.IsCancel = true;
            var bar = new WrapPanel { Margin = new Thickness(11, 10) };
            foreach (var b in new Control[] { copy, term, rb, close }) bar.Children.Add(b);
            bar.Children.Add(new TextBlock { Text = "O comando já vai copiado: cole na janela do terminal.", Foreground = Pal.BMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) });
            var dock = new DockPanel();
            DockPanel.SetDock(lbl, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom);
            dock.Children.Add(lbl); dock.Children.Add(bar); dock.Children.Add(box);
            Content = dock;
        }
    }
}
