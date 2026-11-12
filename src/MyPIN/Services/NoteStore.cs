using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using MyPIN.Models;

namespace MyPIN.Services;

/// <summary>Guarda as notas em %AppData%\MyPIN\notes.json com salvamento automático.</summary>
public sealed class NoteStore
{
    /// <summary>
    /// Pasta dos dados. A variável de ambiente MYPIN_DATA permite usar outra pasta
    /// (modo de teste: roda separado, sem tocar nas notas de verdade).
    /// </summary>
    public static readonly string DataDir =
        Environment.GetEnvironmentVariable("MYPIN_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyPIN");

    public static bool IsTestMode => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MYPIN_DATA"));

    public string FilePath => Path.Combine(DataDir, "notes.json");
    string BackupPath => Path.Combine(DataDir, "notes.bak.json");

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    readonly DispatcherTimer _saveTimer;

    public List<Note> Notes { get; private set; } = new();

    /// <summary>Disparado quando notas são criadas, excluídas, restauradas etc.</summary>
    public event Action? Changed;

    public NoteStore()
    {
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _saveTimer.Tick += (_, _) => SaveNow();
    }

    public void Load()
    {
        Directory.CreateDirectory(DataDir);

        StoreData? data = null;
        if (File.Exists(FilePath))
        {
            data = TryRead(FilePath);
            if (data != null)
            {
                File.Copy(FilePath, BackupPath, overwrite: true);
            }
            else
            {
                // Arquivo corrompido: guarda uma cópia e tenta o backup
                File.Copy(FilePath, Path.Combine(DataDir, $"notes.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
                data = TryRead(BackupPath);
            }
        }

        Notes = data?.Notes ?? new List<Note>();
        Notebooks = data?.Notebooks ?? new List<Notebook>();
        Settings = data?.Settings ?? new AppSettings();
        EnsureDefaultNotebook();
        PurgeExpiredTrash();
        Sounds.MigrateOrder(this);
    }

    public AppSettings Settings { get; private set; } = new();

    // ---------- Lixeira ----------

    public int TrashCount => Notes.Count(n => n.IsDeleted);

    /// <summary>Notas que seriam apagadas se o prazo da lixeira fosse <paramref name="days"/>.</summary>
    public int CountExpired(int days) =>
        days <= 0 ? 0 : Notes.Count(n => n.IsDeleted && (n.DeletedAt ?? n.Modified) < DateTime.Now.AddDays(-days));

    /// <summary>Apaga definitivamente o que passou do prazo da lixeira. Retorna quantas apagou.</summary>
    public int PurgeExpiredTrash()
    {
        int days = Settings.TrashRetentionDays;
        if (days <= 0) return 0;
        var limit = DateTime.Now.AddDays(-days);
        int removed = Notes.RemoveAll(n => n.IsDeleted && (n.DeletedAt ?? n.Modified) < limit);
        if (removed > 0)
        {
            MarkDirty();
            NotifyChanged();
        }
        return removed;
    }

    public static string RetentionLabel(int days) => days switch
    {
        <= 0 => "para sempre",
        1 => "1 dia",
        365 => "1 ano",
        _ => $"{days} dias",
    };

    // ---------- Cadernos ----------

    public List<Notebook> Notebooks { get; private set; } = new();

    public Notebook DefaultNotebook => Notebooks.First(n => n.IsDefault);

    void EnsureDefaultNotebook()
    {
        if (!Notebooks.Any(n => n.IsDefault))
            Notebooks.Insert(0, new Notebook { Name = "Geral", IsDefault = true, Color = "Yellow" });
        // só pode haver um padrão
        foreach (var extra in Notebooks.Where(n => n.IsDefault).Skip(1)) extra.IsDefault = false;
    }

    /// <summary>Caderno da nota (o padrão, se o dela não existir mais).</summary>
    public Notebook GetNotebook(Note note) =>
        Notebooks.FirstOrDefault(n => n.Id == note.NotebookId) ?? DefaultNotebook;

    /// <summary>Cadernos na ordem de exibição: padrão primeiro, depois por nome.</summary>
    public IEnumerable<Notebook> OrderedNotebooks =>
        Notebooks.OrderByDescending(n => n.IsDefault).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase);

    public void DeleteNotebook(Notebook nb)
    {
        if (nb.IsDefault) return;
        foreach (var n in Notes.Where(n => n.NotebookId == nb.Id)) n.NotebookId = DefaultNotebook.Id;
        Notebooks.Remove(nb);
        MarkDirty();
        NotifyChanged();
    }

    static StoreData? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<StoreData>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }

    public void MarkDirty()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void NotifyChanged() => Changed?.Invoke();

    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(new StoreData { Settings = Settings, Notebooks = Notebooks, Notes = Notes }, JsonOpts);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    public void Export(string path)
    {
        SaveNow();
        File.Copy(FilePath, path, overwrite: true);
    }

    /// <summary>Importa notas de um backup, sem duplicar as que já existem.</summary>
    public int Import(string path)
    {
        var data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(path), JsonOpts)
                   ?? throw new InvalidDataException("Arquivo de backup inválido.");
        var existingNb = Notebooks.Select(n => n.Id).ToHashSet();
        foreach (var nb in data.Notebooks.Where(n => !n.IsDefault && !existingNb.Contains(n.Id)))
            Notebooks.Add(nb);
        var existing = Notes.Select(n => n.Id).ToHashSet();
        int count = 0;
        foreach (var n in data.Notes.Where(n => !existing.Contains(n.Id)))
        {
            n.IsOpen = false;
            Notes.Add(n);
            count++;
        }
        SaveNow();
        NotifyChanged();
        return count;
    }
}

public static class Log
{
    public static void Error(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(NoteStore.DataDir);
            File.AppendAllText(Path.Combine(NoteStore.DataDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { /* nada a fazer */ }
    }
}
