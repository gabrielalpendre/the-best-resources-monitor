// Aba "Otimização": ajustes consagrados e reversíveis de cada sistema (energia, gráficos, serviços sem uso, apps no login).
// Cada item diz o estado atual, o que muda e como desfazer. O que é do usuário o app aplica direto; o que precisa de
// administrador vira um comando pronto para colar no terminal (o app nunca roda nada elevado).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EvidenceMonitor
{
    class OptItem
    {
        public string Group, Title, Why, Note, State;
        public bool Done;           // ja esta no estado recomendado/aplicado
        public bool Recommended;    // false = opcional (depende do uso da pessoa)
        public Func<string> Apply, Undo;            // acao direta, sem admin: devolve null ou a mensagem de erro
        public string ApplyAdmin, UndoAdmin;        // comando de administrador (quando nao ha acao direta)

        public string Status { get { return Done ? "OK" : Recommended ? "Recomendado" : "Opcional"; } }
    }

    class OptimizeView : DockPanel
    {
        readonly Table list = new Table();
        readonly TextBlock info = Pal.Label("");
        List<OptItem> items = new List<OptItem>();
        bool loading, loadedOnce;

        public OptimizeView()
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6) };
            bar.Children.Add(Pal.Btn("Aplicar", () => Run(true), null, true));
            bar.Children.Add(Pal.Btn("Desfazer", () => Run(false)));
            bar.Children.Add(Pal.Btn("Detalhes", ShowDetail));
            bar.Children.Add(Pal.Btn("Analisar de novo", Reload));
            info.Margin = new Thickness(10, 0, 0, 0);
            bar.Children.Add(info);
            DockPanel.SetDock(bar, Dock.Top);

            list.Columns.Add(new TableColumn("Situação", 130));
            list.Columns.Add(new TableColumn("Área", 150));
            list.Columns.Add(new TableColumn("Ajuste", 330));
            list.Columns.Add(new TableColumn("Estado atual", 230));
            list.Columns.Add(new TableColumn("O que melhora", 700));
            list.CellOf = (o, c) =>
            {
                var it = o as OptItem;
                if (it == null) return c == 2 ? Cell.Of((string)((object[])o)[0], Pal.BMuted) : null;
                switch (c)
                {
                    case 0: return new Cell { Text = it.Status, Fg = it.Done ? Pal.BInk2 : it.Recommended ? Pal.BWarnText : Pal.BInk2, Bold = !it.Done && it.Recommended, Dot = it.Done ? Pal.Good : it.Recommended ? Pal.Warn : Pal.Axis };
                    case 1: return Cell.Of(it.Group, Pal.BInk2);
                    case 2: return Cell.Of(it.Title, Pal.BInk, !it.Done && it.Recommended);
                    case 3: return Cell.Of(it.State, Pal.BInk2);
                    case 4: return Cell.Of(it.Why, Pal.BInk2);
                }
                return null;
            };
            list.Activated += ShowDetail;
            var ctx = new ContextMenu();
            foreach (var p in new[] { new KeyValuePair<string, Action>("Aplicar", () => Run(true)), new KeyValuePair<string, Action>("Desfazer", () => Run(false)), new KeyValuePair<string, Action>("Detalhes", ShowDetail) })
            { var m = new MenuItem { Header = p.Key }; var a = p.Value; m.Click += (s, e) => a(); ctx.Items.Add(m); }
            list.ContextMenu = ctx;

            Children.Add(bar);
            Children.Add(new Border { Child = list, BorderBrush = Pal.BGrid, BorderThickness = new Thickness(0, 1, 0, 0) });
        }

        public void EnsureLoaded() { if (!loadedOnce) Reload(); }

        public void Reload()
        {
            if (loading) return;
            loading = true;
            if (!loadedOnce) list.SetRows(new object[] { new object[] { "Analisando o sistema..." } });
            var sel = (list.Selected as OptItem)?.Title;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<OptItem> l;
                try { l = Plat.I.Optimizations() ?? new List<OptItem>(); } catch (Exception ex) { l = new List<OptItem>(); ActionLog.Log("erro", "otimização: análise falhou", new Dictionary<string, object> { { "erro", ex.Message } }); }
                // recomendados primeiro, depois opcionais, e o que ja esta ok no fim
                l = l.OrderBy(x => x.Done ? 2 : x.Recommended ? 0 : 1).ToList();
                Dispatcher.UIThread.Post(() =>
                {
                    items = l; loading = false; loadedOnce = true;
                    list.SetRows(items.Count > 0 ? items.Cast<object>() : new object[] { new object[] { "Nenhum ajuste disponível para este sistema." } });
                    var again = items.FirstOrDefault(x => x.Title == sel); if (again != null) list.Select(again, false);
                    int rec = items.Count(x => !x.Done && x.Recommended), opt = items.Count(x => !x.Done && !x.Recommended), ok = items.Count(x => x.Done);
                    info.Text = rec + " recomendado(s) · " + opt + " opcional(is) · " + ok + " já ok  ·  Só ajustes consagrados e reversíveis: tudo tem \"Desfazer\".";
                });
            });
        }

        OptItem Sel()
        {
            var it = list.Selected as OptItem;
            if (it == null) _ = Dialog.Info("Selecione um ajuste na lista.", TopLevel.GetTopLevel(this) as Window);
            return it;
        }

        static string Detail(OptItem it)
        {
            var sb = new StringBuilder();
            sb.AppendLine(it.Title + "  ·  " + it.Group + "  ·  " + it.Status);
            sb.AppendLine();
            sb.AppendLine("Estado atual: " + it.State);
            sb.AppendLine();
            sb.AppendLine("O que melhora: " + it.Why);
            if (!string.IsNullOrEmpty(it.Note)) { sb.AppendLine(); sb.AppendLine("Atenção: " + it.Note); }
            sb.AppendLine();
            sb.AppendLine("Aplicar: " + (it.Apply != null ? "o monitor aplica direto (não precisa de administrador)" : it.ApplyAdmin != null ? "comando de administrador:\n  " + it.ApplyAdmin.Replace("\n", "\n  ") : "—"));
            sb.AppendLine("Desfazer: " + (it.Undo != null ? "o monitor desfaz direto" : it.UndoAdmin != null ? "comando de administrador:\n  " + it.UndoAdmin.Replace("\n", "\n  ") : "—"));
            return sb.ToString();
        }

        void ShowDetail()
        {
            var it = Sel(); if (it == null) return;
            Dialog.Text("Otimização · " + it.Title, Detail(it), TopLevel.GetTopLevel(this) as Window, 760, 400);
        }

        async void Run(bool apply)
        {
            var it = Sel(); if (it == null) return;
            var owner = TopLevel.GetTopLevel(this) as Window;
            Func<string> act = apply ? it.Apply : it.Undo;
            string admin = apply ? it.ApplyAdmin : it.UndoAdmin;
            if (act == null && admin == null) { await Dialog.Info("Este ajuste não tem como ser " + (apply ? "aplicado" : "desfeito") + " por aqui.", owner); return; }
            if (apply && it.Done) { await Dialog.Info("\"" + it.Title + "\" já está aplicado.", owner); return; }

            string verb = apply ? "Aplicar" : "Desfazer";
            if (act == null)
            {
                ActionLog.Log("acao", "otimização: comando de administrador mostrado (" + verb.ToLowerInvariant() + ")", new Dictionary<string, object> { { "ajuste", it.Title }, { "comando", admin } });
                new AdminCmdWindow(verb + ": " + it.Title, (apply ? it.Why : "Volta ao estado anterior.") + (string.IsNullOrEmpty(it.Note) ? "" : "\n\nAtenção: " + it.Note), admin, Reload).Show();
                return;
            }
            if (!await Dialog.Confirm(verb + " \"" + it.Title + "\"?\n\n" + (apply ? it.Why : "Volta ao estado anterior.") + (string.IsNullOrEmpty(it.Note) ? "" : "\n\nAtenção: " + it.Note) + (apply ? "\n\nDá para voltar atrás pelo botão Desfazer." : ""), owner, false, verb, "Cancelar")) return;
            string err = null;
            await System.Threading.Tasks.Task.Run(() => { try { err = act(); } catch (Exception ex) { err = ex.Message; } });
            ActionLog.Log("acao", "otimização " + (apply ? "aplicada" : "desfeita") + ": " + it.Title, new Dictionary<string, object> { { "resultado", err ?? "ok" } });
            if (err != null) await Dialog.Info("Não deu certo: " + err, owner);
            Reload();
        }
    }

    // Comando de administrador para colar no terminal (copia e abre o terminal; o app nao executa nada elevado)
    class AdminCmdWindow : Window
    {
        public AdminCmdWindow(string title, string text, string cmd, Action afterClose)
        {
            var P = Plat.I; bool win = OperatingSystem.IsWindows();
            Title = title;
            Width = 760; Height = 420; MinWidth = 520; MinHeight = 300; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Pal.BSurface; FontFamily = Pal.Font; FontSize = 13;
            var lbl = new TextBlock
            {
                Margin = new Thickness(14, 12, 14, 8), TextWrapping = TextWrapping.Wrap, Foreground = Pal.BInk,
                Text = text + "\n\n" + (win ? "Precisa de administrador: rode o comando num PowerShell como administrador." : "Precisa de administrador: rode o comando num terminal (vai pedir sua senha).") +
                       " Depois clique em \"Analisar de novo\" na aba Otimização."
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
            var close = Pal.Btn("Fechar", Close); close.IsCancel = true;
            var bar = new WrapPanel { Margin = new Thickness(11, 10) };
            foreach (var b in new Control[] { copy, term, close }) bar.Children.Add(b);
            bar.Children.Add(new TextBlock { Text = "O comando já vai copiado: cole na janela do terminal.", Foreground = Pal.BMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) });
            var dock = new DockPanel();
            DockPanel.SetDock(lbl, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom);
            dock.Children.Add(lbl); dock.Children.Add(bar); dock.Children.Add(box);
            Content = dock;
            Closed += (s, e) => afterClose?.Invoke();
        }
    }
}
