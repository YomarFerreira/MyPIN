using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MyPIN.Models;
using MyPIN.Services;

namespace MyPIN;

public partial class ReminderDialog : Window
{
    /// <summary>Data escolhida; null significa "sem lembrete".</summary>
    public DateTime? Result { get; private set; }
    public ReminderRepeat Repeat { get; private set; }
    /// <summary>Som escolhido; null = som padrão.</summary>
    public string? Sound { get; private set; }

    const string DefaultSoundId = "__default__";

    public ReminderDialog(Note note)
    {
        InitializeComponent();
        var initial = note.ReminderBase ?? note.ReminderAt ?? DateTime.Now.AddHours(1);
        if (note.ReminderAt == null) initial = DateTime.Now.AddHours(1);
        DateBox.SelectedDate = initial.Date;
        TimeBox.Text = initial.ToString("HH:mm");
        RemoveButton.Visibility = note.ReminderAt == null ? Visibility.Collapsed : Visibility.Visible;

        foreach (ReminderRepeat r in Enum.GetValues<ReminderRepeat>())
            RepeatBox.Items.Add(new ComboBoxItem { Content = Reminders.Describe(r), Tag = r });
        RepeatBox.SelectedIndex = (int)note.ReminderRepeat;

        var defaultName = Sounds.NameOf(App.Store.Settings.ReminderSound);
        SoundBox.Items.Add(new ComboBoxItem { Content = $"Padrão ({defaultName})", Tag = DefaultSoundId });
        foreach (var s in Sounds.All())
            SoundBox.Items.Add(new ComboBoxItem { Content = s.Name, Tag = s.Id });
        SoundBox.SelectedItem = SoundBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == (note.ReminderSound ?? DefaultSoundId)) ?? SoundBox.Items[0];

        Loaded += (_, _) => { TimeBox.Focus(); TimeBox.SelectAll(); };
        Closed += (_, _) => Sounds.StopPreview();
    }

    void CollectOptions()
    {
        Repeat = (ReminderRepeat)((ComboBoxItem)RepeatBox.SelectedItem).Tag;
        var id = (string)((ComboBoxItem)SoundBox.SelectedItem).Tag;
        Sound = id == DefaultSoundId ? null : id;
    }

    void Preview_Click(object sender, RoutedEventArgs e)
    {
        var id = (string)((ComboBoxItem)SoundBox.SelectedItem).Tag;
        Sounds.Preview(id == DefaultSoundId ? App.Store.Settings.ReminderSound : id, App.Store.Settings.ReminderVolume);
    }

    void Quick_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        var today = DateTime.Today;
        Result = (string)((Button)sender).Tag switch
        {
            "m15" => now.AddMinutes(15),
            "h1" => now.AddHours(1),
            "h3" => now.AddHours(3),
            "today18" => today.AddHours(18) > now ? today.AddHours(18) : today.AddDays(1).AddHours(18),
            "tomorrow9" => today.AddDays(1).AddHours(9),
            "monday9" => NextMonday(today).AddHours(9),
            _ => null,
        };
        if (Result is DateTime r) Result = Reminders.RoundToMinute(r); // minuto cheio: toca sem atraso
        CollectOptions();
        DialogResult = true;
    }

    static DateTime NextMonday(DateTime d)
    {
        int days = ((int)DayOfWeek.Monday - (int)d.DayOfWeek + 7) % 7;
        return d.AddDays(days == 0 ? 7 : days);
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DateBox.SelectedDate is not DateTime date)
        {
            ShowError("Escolha uma data.");
            return;
        }
        if (!TryParseTime(TimeBox.Text, out var time))
        {
            ShowError("Hora inválida. Use por exemplo 14:30 ou 9h.");
            return;
        }
        CollectOptions();
        var when = date.Date + time;
        // Num lembrete repetido, uma data no passado vale como base: pula para a próxima ocorrência
        if (when <= DateTime.Now && Repeat != ReminderRepeat.None)
            when = Reminders.Next(when, Repeat, DateTime.Now);
        if (when <= DateTime.Now)
        {
            ShowError("Escolha um horário no futuro.");
            return;
        }
        Result = when;
        DialogResult = true;
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = true;
    }

    void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    static bool TryParseTime(string input, out TimeSpan time)
    {
        var s = input.Trim().ToLowerInvariant().Replace('h', ':').Replace('.', ':');
        if (s.EndsWith(':')) s += "00";
        if (!s.Contains(':'))
        {
            if (s.Length <= 2) s += ":00";                       // "9" -> 9:00
            else if (s.Length is 3 or 4) s = s[..^2] + ":" + s[^2..]; // "0815" -> 08:15
        }
        return TimeSpan.TryParseExact(s, new[] { @"h\:mm", @"hh\:mm", @"h\:m" }, CultureInfo.InvariantCulture, out time)
               && time < TimeSpan.FromDays(1);
    }
}
