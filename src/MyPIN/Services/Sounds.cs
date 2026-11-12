using System.IO;
using System.Media;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace MyPIN.Services;

/// <summary>Um som da lista. Id: "app:Toque N.wav" (sons do MyPIN), "user" (o som do usuário) ou "none".</summary>
public sealed record SoundItem(string Id, string Name)
{
    public bool IsUser => Id == Sounds.UserId;
    public override string ToString() => Name;
}

/// <summary>
/// Sons dos lembretes: os arquivos da pasta "Sons" do MyPIN (Toque 1, Toque 2, ...) e UM som do
/// usuário, em WAV, com tempo e tamanho limitados. Tudo é tocado com o SoundPlayer do Windows;
/// o volume é aplicado no próprio áudio.
/// </summary>
public static class Sounds
{
    public const string None = "none";
    public const string UserId = "user";
    public const string DefaultId = "app:Toque 1.wav";

    // Regras do som do usuário
    public const int MaxUserSeconds = 10;
    public const int MaxUserKb = 2048;
    public static string UserRules => $"Arquivo WAV, com até {MaxUserSeconds} segundos e até {MaxUserKb / 1024} MB.";

    static string AppDir => Path.Combine(AppContext.BaseDirectory, "Sons");
    static string UserFile => Path.Combine(NoteStore.DataDir, "Sons", "meu-som.wav");

    /// <summary>Ids das versões anteriores → toques atuais.</summary>
    static readonly Dictionary<string, string> OldIds = new()
    {
        ["win:Alarm01.wav"] = "app:Toque 10.wav", ["win:Alarm02.wav"] = "app:Toque 11.wav",
        ["win:Alarm04.wav"] = "app:Toque 12.wav", ["win:Alarm05.wav"] = "app:Toque 1.wav",
        ["win:Alarm06.wav"] = "app:Toque 2.wav", ["win:Alarm07.wav"] = "app:Toque 3.wav",
        ["win:Alarm08.wav"] = "app:Toque 4.wav", ["mp:bipe"] = "app:Toque 5.wav",
        ["mp:carrilhao"] = "app:Toque 6.wav", ["mp:gotas"] = "app:Toque 7.wav",
        ["mp:marimba"] = "app:Toque 8.wav", ["mp:sino"] = "app:Toque 9.wav",
    };

    /// <summary>
    /// Renumeração dos toques (versão 2): o antigo 4 virou o 1 (padrão), os seguintes subiram e os
    /// três mais estridentes (antigos 1, 2 e 3) foram para o fim. Número antigo → número novo.
    /// </summary>
    static readonly Dictionary<int, int> Renumber = new()
    {
        [4] = 1, [5] = 2, [6] = 3, [7] = 4, [8] = 5, [9] = 6, [10] = 7, [11] = 8, [12] = 9, [1] = 10, [2] = 11, [3] = 12,
    };

    public const int CurrentSoundOrder = 2;

    /// <summary>
    /// Ao abrir dados de antes da renumeração: cada nota continua com o MESMO som que tinha
    /// (só muda o número), e o som padrão passa a ser o novo Toque 1.
    /// </summary>
    public static void MigrateOrder(NoteStore store)
    {
        if (store.Settings.SoundOrder >= CurrentSoundOrder) return;
        static string? Map(string? id)
        {
            if (id == null || !id.StartsWith("app:Toque ") || !id.EndsWith(".wav")) return id;
            return int.TryParse(id[10..^4], out int n) && Renumber.TryGetValue(n, out int m) ? $"app:Toque {m}.wav" : id;
        }
        foreach (var note in store.Notes) note.ReminderSound = Map(note.ReminderSound);
        store.Settings.ReminderSound = DefaultId;
        store.Settings.SoundOrder = CurrentSoundOrder;
        store.MarkDirty();
    }

    /// <summary>Sons da pasta do app, em ordem natural (Toque 2 antes de Toque 10).</summary>
    static List<SoundItem> AppSounds()
    {
        try
        {
            if (!Directory.Exists(AppDir)) return new();
            return Directory.GetFiles(AppDir, "*.wav")
                .Select(f => Path.GetFileName(f))
                .OrderBy(f => Regex.Replace(f, @"\d+", m => m.Value.PadLeft(6, '0')), StringComparer.CurrentCultureIgnoreCase)
                .Select(f => new SoundItem("app:" + f, Path.GetFileNameWithoutExtension(f)))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return new();
        }
    }

    /// <summary>Ordem: som do usuário (se houver), sons do MyPIN e, por último, "sem som".</summary>
    public static List<SoundItem> All()
    {
        var list = new List<SoundItem>();
        if (HasUserSound) list.Add(new(UserId, "★ " + UserSoundName));
        list.AddRange(AppSounds());
        list.Add(new(None, "(sem som)"));
        return list;
    }

    public static bool HasUserSound => File.Exists(UserFile);

    static string UserSoundName =>
        App.Store?.Settings.UserSoundName is { Length: > 0 } n ? n : "Meu som";

    /// <summary>Id válido: um som que existe; senão o som padrão.</summary>
    public static string Resolve(string? id)
    {
        if (id == null) return DefaultId;
        if (id.StartsWith("sys:")) id = "win:" + id[4..];
        if (OldIds.TryGetValue(id, out var mapped)) id = mapped;
        if (id == None || id == UserId && HasUserSound) return id;
        if (id.StartsWith("app:") && File.Exists(Path.Combine(AppDir, id[4..]))) return id;
        return DefaultId;
    }

    public static string NameOf(string? id) => All().FirstOrDefault(s => s.Id == Resolve(id))?.Name ?? "";

    // ------------------------------------------------------------------
    // Som do usuário
    // ------------------------------------------------------------------

    /// <summary>Confere o arquivo e o guarda como o som do usuário (substitui o anterior).</summary>
    public static void SetUserSound(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new InvalidOperationException("Arquivo não encontrado.");
        if (!info.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O som precisa ser um arquivo .wav.\n" + UserRules);
        if (info.Length > MaxUserKb * 1024L)
            throw new InvalidOperationException($"O arquivo tem {info.Length / 1024} KB; o máximo é {MaxUserKb} KB.\n" + UserRules);

        var wav = Wav.Read(File.ReadAllBytes(sourcePath)); // lança erro se não for WAV válido
        if (wav.Seconds > MaxUserSeconds + 0.05)
            throw new InvalidOperationException($"O som dura {wav.Seconds:0.0} segundos; o máximo é {MaxUserSeconds}.\n" + UserRules);

        Directory.CreateDirectory(Path.GetDirectoryName(UserFile)!);
        File.Copy(sourcePath, UserFile, overwrite: true);
        App.Store.Settings.UserSoundName = Path.GetFileNameWithoutExtension(sourcePath);
        App.Store.MarkDirty();
    }

    public static void RemoveUserSound()
    {
        if (File.Exists(UserFile)) File.Delete(UserFile);
        App.Store.Settings.UserSoundName = "";
        foreach (var n in App.Store.Notes.Where(n => n.ReminderSound == UserId)) n.ReminderSound = null;
        if (App.Store.Settings.ReminderSound == UserId) App.Store.Settings.ReminderSound = DefaultId;
        App.Store.MarkDirty();
    }

    // ------------------------------------------------------------------
    // Tocar
    // ------------------------------------------------------------------

    /// <summary>
    /// Silêncio no começo de cada som. Fones e caixas Bluetooth (e algumas placas de som em economia
    /// de energia) "dormem" e levam até ~1 s para acordar: sem isso, o começo do som se perde.
    /// </summary>
    const double WakeUpSeconds = 1.0;

    static SoundPlayer? _preview;

    /// <summary>Monta o WAV (em memória) do som, já com o volume aplicado.</summary>
    static byte[]? Render(string? id, int volume, double gapSeconds, double leadSeconds = WakeUpSeconds)
    {
        id = Resolve(id);
        if (id == None) return null;
        try
        {
            var path = id == UserId ? UserFile : Path.Combine(AppDir, id[4..]);
            var pcm = Wav.Read(File.ReadAllBytes(path));
            return Wav.Write(pcm, Math.Clamp(volume, 0, 100) / 100f, gapSeconds, leadSeconds);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }

    /// <summary>Toca uma vez (botão ▶ "ouvir").</summary>
    public static void Preview(string? id, int volume)
    {
        StopPreview();
        var bytes = Render(id, volume, 0);
        if (bytes == null) return;
        _preview = new SoundPlayer(new MemoryStream(bytes));
        _preview.Play();
    }

    public static void StopPreview()
    {
        _preview?.Stop();
        _preview?.Dispose();
        _preview = null;
    }

    /// <summary>Toca em repetição até Stop() ou o tempo máximo.</summary>
    public static LoopingSound? PlayLoop(string? id, int volume, TimeSpan maxDuration)
    {
        var bytes = Render(id, volume, 0.2); // + 1 s de "despertar" no começo = ~1,2 s entre repetições
        return bytes == null ? null : new LoopingSound(bytes, maxDuration);
    }
}

public sealed class LoopingSound
{
    readonly SoundPlayer _player;
    readonly DispatcherTimer _limit;
    bool _stopped;

    public LoopingSound(byte[] wav, TimeSpan maxDuration)
    {
        _player = new SoundPlayer(new MemoryStream(wav));
        _player.PlayLooping();
        _limit = new DispatcherTimer { Interval = maxDuration };
        _limit.Tick += (_, _) => Stop();
        _limit.Start();
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _limit.Stop();
        _player.Stop();
        _player.Dispose();
    }
}

// ======================================================================
// WAV: leitura (só PCM 8/16 bits) e escrita (16 bits) com volume
// ======================================================================

static class Wav
{
    public sealed record Pcm(float[] Samples, int Channels, int SampleRate)
    {
        public double Seconds => Samples.Length / (double)Channels / SampleRate;
    }

    public static Pcm Read(byte[] data)
    {
        static Exception Bad(string why) => new InvalidOperationException($"Arquivo WAV não suportado: {why}\n{Sounds.UserRules}");
        if (data.Length < 44 || ReadTag(data, 0) != "RIFF" || ReadTag(data, 8) != "WAVE") throw Bad("não é um WAV válido.");

        int channels = 0, rate = 0, bits = 0, format = 0;
        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            var tag = ReadTag(data, pos);
            int size = BitConverter.ToInt32(data, pos + 4);
            int body = pos + 8;
            if (size < 0 || body + size > data.Length) size = data.Length - body;
            if (tag == "fmt ")
            {
                format = BitConverter.ToInt16(data, body);
                channels = BitConverter.ToInt16(data, body + 2);
                rate = BitConverter.ToInt32(data, body + 4);
                bits = BitConverter.ToInt16(data, body + 14);
            }
            else if (tag == "data")
            {
                if (format != 1 && format != unchecked((short)0xFFFE)) throw Bad("o áudio está comprimido (use WAV PCM, sem compressão).");
                if (bits != 8 && bits != 16) throw Bad($"{bits} bits por amostra (use 8 ou 16 bits).");
                if (channels is < 1 or > 2) throw Bad("use áudio mono ou estéreo.");
                if (rate is < 8000 or > 48000) throw Bad("taxa de amostragem fora do comum.");
                int bytesPer = bits / 8;
                var samples = new float[size / bytesPer];
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = bits == 16
                        ? BitConverter.ToInt16(data, body + i * 2) / 32768f
                        : (data[body + i] - 128) / 128f;
                }
                return new Pcm(samples, channels, rate);
            }
            pos = body + size + (size & 1);
        }
        throw Bad("não encontrei o áudio dentro do arquivo.");
    }

    static string ReadTag(byte[] d, int i) => System.Text.Encoding.ASCII.GetString(d, i, 4);

    public static byte[] Write(Pcm pcm, float volume, double gapSeconds, double leadSeconds = 0)
    {
        int gap = (int)(gapSeconds * pcm.SampleRate) * pcm.Channels;
        int lead = (int)(leadSeconds * pcm.SampleRate) * pcm.Channels;
        int n = lead + pcm.Samples.Length + gap;
        using var ms = new MemoryStream(44 + n * 2);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)pcm.Channels);
        w.Write(pcm.SampleRate); w.Write(pcm.SampleRate * pcm.Channels * 2); w.Write((short)(pcm.Channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        for (int i = 0; i < lead; i++) w.Write((short)0);
        foreach (var s in pcm.Samples) w.Write((short)Math.Clamp(s * volume * 32767f, -32768f, 32767f));
        for (int i = 0; i < gap; i++) w.Write((short)0);
        w.Flush();
        return ms.ToArray();
    }
}
