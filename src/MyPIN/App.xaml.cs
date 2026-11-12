using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MyPIN.Models;
using MyPIN.Services;
using MyPIN.Services.Ai;

namespace MyPIN;

public partial class App : Application
{
    const string MutexName = "MyPIN_SingleInstance_v1";
    const string ShowEventName = "MyPIN_ShowNotes_v1";

    public static new App Current => (App)Application.Current;
    public static NoteStore Store { get; private set; } = null!;

    readonly Dictionary<Guid, NoteWindow> _windows = new();
    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    TrayIcon? _tray;
    HotkeyManager? _hotkeys;
    DispatcherTimer? _reminderTimer;
    ManagerWindow? _manager;
    bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Apenas uma instância: se já estiver aberto, pede para a instância existente mostrar as notas.
        // (No modo de teste — MYPIN_DATA — roda como instância separada.)
        var suffix = NoteStore.IsTestMode
            ? "_test" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(NoteStore.DataDir.ToLowerInvariant())))[..12]
            : "";
        _mutex = new Mutex(true, MutexName + suffix, out bool isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName + suffix).Set(); } catch { }
            Shutdown();
            return;
        }
        // nome novo: copia as notas do MyPostIt (uma vez só) e acerta o "iniciar com o Windows"
        if (!Migration.Run())
        {
            Shutdown();
            return;
        }
        _showEvent =new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName + suffix);
        new Thread(() =>
        {
            try { while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(ShowAll); }
            catch { /* encerrando */ }
        }) { IsBackground = true }.Start();

        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error(ex.Exception);
            ex.Handled = true;
        };
        SessionEnding += (_, _) => SaveEverything();

        Theme.Init();

        Store = new NoteStore();
        Store.Load();
        Ai = AiConfig.Load();

        foreach (var note in Store.Notes.Where(n => n.IsOpen && !n.IsDeleted && !Store.GetNotebook(n).Hidden).ToList())
            OpenNote(note, activate: false);

        if (Store.Notes.Count == 0)
            CreateNote();

        _tray = new TrayIcon(this);

        _hotkeys = new HotkeyManager();
        _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Key.N, () => CreateNote());
        _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Key.S, ShowAll);

        // Lembretes: confere uma vez por minuto, exatamente no segundo :00 (os lembretes são em
        // horas e minutos, então não há atraso, e o app só "acorda" 1 vez por minuto).
        _reminderTimer = new DispatcherTimer();
        _reminderTimer.Tick += (_, _) =>
        {
            CheckReminders();
            ScheduleNextMinute();
        };
        // PC voltou do repouso ou relógio do Windows foi ajustado: confere e realinha na hora
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.TimeChanged += OnTimeChanged;
        CheckReminders();
        ScheduleNextMinute();
    }

    /// <summary>Agenda a próxima conferência para o início do próximo minuto (+ 50 ms de folga).</summary>
    void ScheduleNextMinute()
    {
        if (_reminderTimer == null) return;
        var now = DateTime.Now;
        var nextMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind).AddMinutes(1);
        _reminderTimer.Stop();
        _reminderTimer.Interval = nextMinute - now + TimeSpan.FromMilliseconds(50);
        _reminderTimer.Start();
    }

    void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            Dispatcher.BeginInvoke(() => { CheckReminders(); ScheduleNextMinute(); });
    }

    void OnTimeChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { CheckReminders(); ScheduleNextMinute(); });

    // ---------- Notas ----------

    /// <summary>
    /// Cria uma nota. Ela vai para o caderno indicado, ou para o da nota de origem,
    /// ou para o padrão — e nasce com a cor desse caderno.
    /// </summary>
    public NoteWindow CreateNote(Note? from = null, Notebook? notebook = null)
    {
        notebook ??= from != null ? Store.GetNotebook(from) : Store.DefaultNotebook;
        if (notebook.Hidden) SetNotebookHidden(notebook, false); // senão a nota nova nasceria invisível
        var note = new Note { Color = notebook.Color, NotebookId = notebook.Id };
        if (from != null) { note.Width = from.Width; note.Zoom = from.Zoom; }
        PlaceNewNote(note, from);
        Store.Notes.Add(note);
        var w = OpenNote(note, activate: true);
        w.FocusEditor();
        Store.NotifyChanged();
        return w;
    }

    void PlaceNewNote(Note note, Note? from)
    {
        var wa = SystemParameters.WorkArea;
        if (from?.Left is double fl && from.Top is double ft)
        {
            double right = fl + from.Width - 4;
            if (right + note.Width <= wa.Right)
            {
                note.Left = right;
                note.Top = ft;
                return;
            }
        }
        int n = _windows.Count % 10;
        note.Left = wa.Left + (wa.Width - note.Width) / 2 - 120 + n * 26;
        note.Top = wa.Top + (wa.Height - note.Height) / 2 - 120 + n * 26;
    }

    public NoteWindow OpenNote(Note note, bool activate = true)
    {
        if (_windows.TryGetValue(note.Id, out var existing))
        {
            if (!existing.IsVisible) existing.Show();
            if (activate) existing.BringToFront();
            return existing;
        }

        EnsureOnScreen(note);
        note.IsOpen = true;
        var w = new NoteWindow(note) { ShowActivated = activate };
        _windows[note.Id] = w;
        w.Closed += (_, _) => OnNoteWindowClosed(note);
        w.Show();
        if (activate) w.BringToFront();
        Store.MarkDirty();
        return w;
    }

    static void EnsureOnScreen(Note note)
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
        if (note.Left is not double l || note.Top is not double t ||
            l > vr - 60 || l + note.Width < vl + 60 || t < vt - 10 || t > vb - 40)
        {
            var wa = SystemParameters.WorkArea;
            note.Left = wa.Left + (wa.Width - note.Width) / 2;
            note.Top = wa.Top + (wa.Height - note.Height) / 2;
        }
    }

    void OnNoteWindowClosed(Note note)
    {
        _windows.Remove(note.Id);
        if (_exiting) return;
        note.IsOpen = false;
        Store.MarkDirty();
        Store.NotifyChanged();
    }

    public void HideNote(Note note)
    {
        if (_windows.TryGetValue(note.Id, out var w)) w.Close();
        else { note.IsOpen = false; Store.MarkDirty(); Store.NotifyChanged(); }
    }

    public void DeleteNote(Note note)
    {
        if (_windows.TryGetValue(note.Id, out var w)) w.Close(); // salva o conteúdo antes
        note.IsOpen = false;
        if (note.IsEmpty)
        {
            Store.Notes.Remove(note); // nota vazia não precisa ir para a lixeira
        }
        else
        {
            note.IsDeleted = true;
            note.DeletedAt = DateTime.Now;
        }
        Store.MarkDirty();
        Store.NotifyChanged();
    }

    public void RestoreNote(Note note)
    {
        note.IsDeleted = false;
        note.DeletedAt = null;
        OpenNote(note);
        Store.NotifyChanged();
    }

    public void PurgeNote(Note note)
    {
        Store.Notes.Remove(note);
        Store.MarkDirty();
        Store.NotifyChanged();
    }

    public void EmptyTrash()
    {
        Store.Notes.RemoveAll(n => n.IsDeleted);
        Store.MarkDirty();
        Store.NotifyChanged();
    }

    public NoteWindow DuplicateNote(Note source)
    {
        if (_windows.TryGetValue(source.Id, out var sw)) sw.FlushContent();
        var copy = new Note
        {
            Title = source.Title,
            ContentRtf = source.ContentRtf,
            PlainText = source.PlainText,
            Color = source.Color,
            NotebookId = source.NotebookId,
            Width = source.Width,
            Height = source.Height,
            Opacity = source.Opacity,
            Zoom = source.Zoom,
            Left = (source.Left ?? 100) + 28,
            Top = (source.Top ?? 100) + 28,
        };
        Store.Notes.Add(copy);
        var w = OpenNote(copy);
        Store.NotifyChanged();
        return w;
    }

    /// <summary>Esconde temporariamente todas as notas, menos esta ("Mostrar todas" traz de volta).</summary>
    public void HideOthers(Note keep)
    {
        foreach (var w in _windows.Values.Where(w => w.Note.Id != keep.Id)) w.Hide();
    }

    public void MoveNote(Note note, Notebook notebook)
    {
        note.NotebookId = notebook.Id;
        if (notebook.Hidden && _windows.TryGetValue(note.Id, out var w)) w.Hide();
        Store.MarkDirty();
        Store.NotifyChanged();
    }

    /// <summary>Mostra/esconde da tela todas as notas abertas de um caderno.</summary>
    public void SetNotebookHidden(Notebook nb, bool hidden)
    {
        nb.Hidden = hidden;
        if (hidden)
        {
            foreach (var w in _windows.Values.Where(w => Store.GetNotebook(w.Note) == nb)) w.Hide();
        }
        else
        {
            foreach (var n in Store.Notes.Where(n => n.IsOpen && !n.IsDeleted && Store.GetNotebook(n) == nb).ToList())
                OpenNote(n, activate: false);
        }
        Store.MarkDirty();
        Store.NotifyChanged();
    }

    /// <summary>
    /// Mostra todas as notas: as escondidas por "Ocultar todas" e também as ocultadas uma a uma
    /// (botão X / Ctrl+W). Só ficam de fora as excluídas e as de cadernos ocultos da tela.
    /// </summary>
    public void ShowAll()
    {
        var notes = Store.Notes.Where(n => !n.IsDeleted && !Store.GetNotebook(n).Hidden).ToList();
        if (notes.Count == 0)
        {
            ShowManager();
            return;
        }
        foreach (var n in notes)
            OpenNote(n, activate: false).BringToFront();
        Store.NotifyChanged();
    }

    public void HideAll()
    {
        foreach (var w in _windows.Values) w.Hide();
    }

    public void ShowManager()
    {
        if (_manager == null)
        {
            _manager = new ManagerWindow();
            _manager.Closed += (_, _) => _manager = null;
            _manager.Show();
        }
        if (_manager.WindowState == WindowState.Minimized) _manager.WindowState = WindowState.Normal;
        _manager.Activate();
    }

    // ---------- IA ----------

    public AiConfig Ai { get; private set; } = new();

    /// <summary>Disparado quando a configuração da IA muda (as notas mostram/escondem o botão ✨).</summary>
    public event Action? AiChanged;

    /// <summary>Mudou a escolha dos botões da barra de baixo (vale para todas as notas).</summary>
    public event Action? FooterChanged;

    public void SetFooterButton(string key, bool visible)
    {
        var hidden = Store.Settings.HiddenFooterButtons;
        if (visible) hidden.Remove(key);
        else if (!hidden.Contains(key)) hidden.Add(key);
        Store.MarkDirty();
        FooterChanged?.Invoke();
    }

    public void ShowAllFooterButtons()
    {
        Store.Settings.HiddenFooterButtons.Clear();
        Store.MarkDirty();
        FooterChanged?.Invoke();
    }

    AiSettingsWindow? _aiSettings;

    public void ShowAiSettings()
    {
        if (_aiSettings != null) { _aiSettings.Activate(); return; }
        _aiSettings = new AiSettingsWindow();
        _aiSettings.Closed += (_, _) => _aiSettings = null;
        _aiSettings.ShowDialog();
    }

    public void SaveAi(AiConfig cfg)
    {
        cfg.Save();
        Ai = cfg;
        AiChanged?.Invoke();
    }

    // ---------- Lixeira ----------

    public void ShowTrash()
    {
        ShowManager();
        _manager?.ShowTrash();
    }

    /// <summary>Muda o prazo da lixeira. Se isso apagar notas na hora, pede confirmação.</summary>
    public void SetTrashRetention(int days)
    {
        int willDelete = Store.CountExpired(days);
        if (willDelete > 0)
        {
            var msg = $"Com o prazo de {NoteStore.RetentionLabel(days)}, {willDelete} nota(s) que já estão na lixeira " +
                      "há mais tempo serão apagadas definitivamente agora.\n\nContinuar?";
            if (MessageBox.Show(msg, "MyPIN — Lixeira", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }
        Store.Settings.TrashRetentionDays = days;
        Store.PurgeExpiredTrash();
        Store.MarkDirty();
        Store.NotifyChanged();
    }

    // ---------- Lembretes ----------

    void CheckReminders()
    {
        Store.PurgeExpiredTrash(); // aproveita o mesmo relógio para limpar a lixeira vencida

        var now = DateTime.Now;
        foreach (var note in Store.Notes.Where(n => !n.IsDeleted).ToList())
        {
            bool due = false;

            // lembrete adiado (soneca)
            if (note.SnoozeAt is DateTime s && s <= now)
            {
                note.SnoozeAt = null;
                due = true;
            }
            // lembrete agendado: se repete, já agenda o próximo; senão, some
            if (note.ReminderAt is DateTime r && r <= now)
            {
                note.ReminderAt = note.ReminderRepeat != ReminderRepeat.None
                    ? Reminders.Next(note.ReminderBase ?? r, note.ReminderRepeat, now)
                    : null;
                if (note.ReminderAt == null) note.ReminderRepeat = ReminderRepeat.None;
                due = true;
            }
            if (!due) continue;

            Store.MarkDirty();
            var nb = Store.GetNotebook(note);
            if (nb.Hidden) SetNotebookHidden(nb, false); // o lembrete precisa aparecer
            var w = OpenNote(note);
            w.UpdateReminderIcon();
            w.Ring(note.ReminderSound ?? Store.Settings.ReminderSound);
            _tray?.ShowBalloon("⏰ Lembrete", note.DisplayTitle);
            Store.NotifyChanged();
        }
    }

    ReminderSettingsWindow? _reminderSettings;
    AboutWindow? _about;

    public void ShowAbout()
    {
        if (_about != null) { _about.Activate(); return; }
        _about = new AboutWindow();
        _about.Closed += (_, _) => _about = null;
        _about.Show();
    }

    public void ShowReminderSettings()
    {
        if (_reminderSettings != null) { _reminderSettings.Activate(); return; }
        _reminderSettings = new ReminderSettingsWindow();
        _reminderSettings.Closed += (_, _) => _reminderSettings = null;
        _reminderSettings.ShowDialog();
    }

    // ---------- Encerramento ----------

    void SaveEverything()
    {
        foreach (var w in _windows.Values.ToList()) w.FlushContent();
        Store?.SaveNow();
    }

    public void ExitApp()
    {
        SaveEverything();
        _exiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Store != null && !_exiting) SaveEverything();
        _exiting = true;
        _reminderTimer?.Stop();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.TimeChanged -= OnTimeChanged;
        Theme.Shutdown();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _showEvent?.Dispose();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}
