namespace MyPIN.Models;

public class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string ContentRtf { get; set; } = "";
    public string PlainText { get; set; } = "";
    public string Color { get; set; } = "Yellow";

    /// <summary>Caderno da nota. Guid.Empty (ou um caderno que não existe mais) = caderno padrão.</summary>
    public Guid NotebookId { get; set; }

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 296;
    public double Height { get; set; } = 276;

    public bool IsOpen { get; set; } = true;
    public bool Topmost { get; set; }
    public bool RolledUp { get; set; }
    public bool Locked { get; set; }
    /// <summary>Tabelas minimizadas (posição de cada uma entre as tabelas da nota: 0 = 1ª tabela).</summary>
    public List<int> CollapsedTables { get; set; } = new();
    public double Opacity { get; set; } = 1.0;
    public double Zoom { get; set; } = 1.0;

    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>Próximo disparo do lembrete.</summary>
    public DateTime? ReminderAt { get; set; }
    /// <summary>Data/hora escolhida originalmente (base para calcular as repetições sem "escorregar").</summary>
    public DateTime? ReminderBase { get; set; }
    public ReminderRepeat ReminderRepeat { get; set; }
    /// <summary>Som deste lembrete; null = som padrão das configurações.</summary>
    public string? ReminderSound { get; set; }
    /// <summary>Lembrete adiado (soneca): toca de novo nesta hora, sem mexer na repetição.</summary>
    public DateTime? SnoozeAt { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(PlainText);

    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title.Trim();
            var first = PlainText.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return first ?? "(nota vazia)";
        }
    }

    /// <summary>Texto para copiar/compartilhar: título (se houver) + conteúdo.</summary>
    public string ShareText =>
        string.IsNullOrWhiteSpace(Title) ? PlainText : $"{Title.Trim()}\r\n\r\n{PlainText}";
}

public class Notebook
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Color { get; set; } = "Yellow";
    /// <summary>Notas deste caderno ficam escondidas da tela.</summary>
    public bool Hidden { get; set; }
    public bool IsDefault { get; set; }
}

public class AppSettings
{
    /// <summary>Dias que uma nota excluída fica na lixeira. 0 = nunca apaga sozinha.</summary>
    public int TrashRetentionDays { get; set; } = 30;

    /// <summary>Som padrão dos lembretes (ver Services/Sounds.cs).</summary>
    public string ReminderSound { get; set; } = "app:Toque 1.wav";

    /// <summary>Nome (do arquivo original) do som que o usuário colocou.</summary>
    public string UserSoundName { get; set; } = "";

    /// <summary>Volume dos lembretes, 0 a 100.</summary>
    public int ReminderVolume { get; set; } = 80;

    /// <summary>Numeração dos toques em que os dados foram salvos (0 = a antiga). Ver Sounds.MigrateOrder.</summary>
    public int SoundOrder { get; set; }

    /// <summary>Botões escondidos da barra de baixo das notas (chaves de NoteWindow.FooterItems).</summary>
    public List<string> HiddenFooterButtons { get; set; } = new();
}

public enum ReminderRepeat { None, Daily, Weekdays, Weekly, Monthly }

public static class Reminders
{
    public static string Describe(ReminderRepeat r) => r switch
    {
        ReminderRepeat.Daily => "Todo dia",
        ReminderRepeat.Weekdays => "Dias úteis (seg. a sex.)",
        ReminderRepeat.Weekly => "Toda semana",
        ReminderRepeat.Monthly => "Todo mês",
        _ => "Não repetir",
    };

    /// <summary>
    /// Arredonda para o minuto mais próximo (14:15:40 → 14:16:00). Os lembretes são conferidos
    /// no início de cada minuto, então guardá-los em minutos cheios faz tocarem sem atraso.
    /// </summary>
    public static DateTime RoundToMinute(DateTime t)
    {
        var floor = new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, t.Kind);
        return t.Second >= 30 ? floor.AddMinutes(1) : floor;
    }

    /// <summary>Próxima ocorrência depois de <paramref name="after"/>, contando a partir da data base.</summary>
    public static DateTime Next(DateTime baseTime, ReminderRepeat repeat, DateTime after)
    {
        switch (repeat)
        {
            case ReminderRepeat.Daily:
            {
                var t = baseTime;
                if (t <= after) t = t.AddDays(Math.Floor((after - t).TotalDays) + 1);
                while (t <= after) t = t.AddDays(1);
                return t;
            }
            case ReminderRepeat.Weekdays:
            {
                var t = baseTime;
                if (t <= after) t = t.AddDays(Math.Floor((after - t).TotalDays));
                while (t <= after || t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) t = t.AddDays(1);
                return t;
            }
            case ReminderRepeat.Weekly:
            {
                var t = baseTime;
                if (t <= after) t = t.AddDays(7 * (Math.Floor((after - t).TotalDays / 7) + 1));
                while (t <= after) t = t.AddDays(7);
                return t;
            }
            case ReminderRepeat.Monthly:
            {
                // sempre a partir da base: dia 31 vira 30/28 nos meses curtos e volta a 31 depois
                for (int k = 0; ; k++)
                {
                    var t = baseTime.AddMonths(k);
                    if (t > after) return t;
                }
            }
            default:
                return baseTime;
        }
    }
}

public class StoreData
{
    public int Version { get; set; } = 2;
    public AppSettings Settings { get; set; } = new();
    public List<Notebook> Notebooks { get; set; } = new();
    public List<Note> Notes { get; set; } = new();
}
