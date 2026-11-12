using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace MyPIN.Services;

/// <summary>Liga/desliga a inicialização junto com o Windows (HKCU\...\Run).</summary>
public static class StartupManager
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "MyPIN";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Atalhos globais (funcionam mesmo com outro programa em foco).</summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    const int WM_HOTKEY = 0x0312;

    readonly HwndSource _source;
    readonly Dictionary<int, Action> _actions = new();
    int _nextId = 1;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("MyPINHotkeys")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: janela invisível só para mensagens
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    public bool Register(ModifierKeys modifiers, Key key, Action action)
    {
        uint mods = MOD_NOREPEAT;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;

        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, mods, (uint)KeyInterop.VirtualKeyFromKey(key)))
            return false;
        _actions[id] = action;
        return true;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _source.Dispose();
    }
}

/// <summary>Ícone do app (o desenho fica em IconArt.cs), em várias resoluções.</summary>
public static class AppIcon
{
    static byte[]? _ico;
    static byte[] Ico => _ico ??= IconArt.BuildIco();

    /// <summary>Ícone no tamanho mais próximo do pedido (o .ico tem de 16 a 256 px).</summary>
    public static Drawing.Icon Create(int size) => new(new MemoryStream(Ico), new Drawing.Size(size, size));

    /// <summary>Ícone da bandeja no tamanho certo para a escala da tela (100%, 125%, 150%...).</summary>
    public static Drawing.Icon CreateTray() => Create(WinForms.SystemInformation.SmallIconSize.Width);

    public static ImageSource CreateImageSource()
    {
        var decoder = new IconBitmapDecoder(new MemoryStream(Ico), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        frame.Freeze();
        return frame;
    }
}

/// <summary>Ícone na área de notificação (perto do relógio).</summary>
public sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon _icon;

    public TrayIcon(App app)
    {
        var menu = new WinForms.ContextMenuStrip();

        var newItem = new WinForms.ToolStripMenuItem("Nova nota", null, (_, _) => app.CreateNote())
        { ShortcutKeyDisplayString = "Ctrl+Alt+N", Font = new Drawing.Font(WinForms.Control.DefaultFont, Drawing.FontStyle.Bold) };
        menu.Items.Add(newItem);
        menu.Items.Add(new WinForms.ToolStripMenuItem("Mostrar todas", null, (_, _) => app.ShowAll())
        { ShortcutKeyDisplayString = "Ctrl+Alt+S" });
        menu.Items.Add("Ocultar todas", null, (_, _) => app.HideAll());
        menu.Items.Add("Gerenciar notas...", null, (_, _) => app.ShowManager());
        menu.Items.Add(new WinForms.ToolStripSeparator());

        // Cadernos: marcado = notas do caderno aparecem na tela
        var notebooks = new WinForms.ToolStripMenuItem("Cadernos na tela");
        notebooks.DropDownItems.Add("(carregando)"); // necessário para aparecer a seta do submenu
        notebooks.DropDownOpening += (_, _) =>
        {
            notebooks.DropDownItems.Clear();
            foreach (var nb in App.Store.OrderedNotebooks)
            {
                var target = nb;
                var item = new WinForms.ToolStripMenuItem(nb.Name) { Checked = !nb.Hidden };
                item.Click += (_, _) => app.SetNotebookHidden(target, !target.Hidden);
                notebooks.DropDownItems.Add(item);
            }
        };
        menu.Items.Add(notebooks);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        // Lixeira: abrir + prazo para apagar sozinha
        var trash = new WinForms.ToolStripMenuItem("Lixeira");
        trash.DropDownItems.Add("(carregando)");
        trash.DropDownOpening += (_, _) =>
        {
            trash.DropDownItems.Clear();
            int count = App.Store.TrashCount;
            var open = new WinForms.ToolStripMenuItem(count == 0 ? "Abrir lixeira (vazia)" : $"Abrir lixeira ({count})")
            { Font = new Drawing.Font(WinForms.Control.DefaultFont, Drawing.FontStyle.Bold) };
            open.Click += (_, _) => app.ShowTrash();
            trash.DropDownItems.Add(open);
            trash.DropDownItems.Add(new WinForms.ToolStripSeparator());
            trash.DropDownItems.Add(new WinForms.ToolStripMenuItem("Apagar automaticamente depois de:") { Enabled = false });
            foreach (var days in new[] { 7, 15, 30, 60, 90, 365, 0 })
            {
                var d = days;
                var item = new WinForms.ToolStripMenuItem("    " + char.ToUpper(NoteStore.RetentionLabel(d)[0]) + NoteStore.RetentionLabel(d)[1..])
                { Checked = App.Store.Settings.TrashRetentionDays == d };
                if (d == 0) item.Text = "    Nunca (guardar para sempre)";
                item.Click += (_, _) => app.SetTrashRetention(d);
                trash.DropDownItems.Add(item);
            }
        };
        menu.Items.Add(trash);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        menu.Items.Add("⏰ Lembretes (som e volume)...", null, (_, _) => app.ShowReminderSettings());
        menu.Items.Add("✨ Inteligência Artificial...", null, (_, _) => app.ShowAiSettings());
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var startup = new WinForms.ToolStripMenuItem("Iniciar com o Windows") { CheckOnClick = true, Checked = StartupManager.IsEnabled };
        startup.CheckedChanged += (_, _) =>
        {
            try { StartupManager.Set(startup.Checked); }
            catch (Exception ex) { Log.Error(ex); }
        };
        menu.Items.Add(startup);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Sobre o MyPIN...", null, (_, _) => app.ShowAbout());
        menu.Items.Add("Sair", null, (_, _) => app.ExitApp());

        _icon = new WinForms.NotifyIcon
        {
            Icon = AppIcon.CreateTray(),
            Text = NoteStore.IsTestMode ? "MyPIN (TESTE)" : "MyPIN — duplo clique para mostrar as notas",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) app.ShowAll();
        };
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(8000, title, string.IsNullOrWhiteSpace(text) ? " " : text, WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
