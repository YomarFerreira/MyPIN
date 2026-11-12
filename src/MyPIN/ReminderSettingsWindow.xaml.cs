using System.Windows;
using Microsoft.Win32;
using MyPIN.Services;

namespace MyPIN;

public partial class ReminderSettingsWindow : Window
{
    public ReminderSettingsWindow()
    {
        InitializeComponent();
        Icon = AppIcon.CreateImageSource();
        RulesText.Text = "Se você escolher outro arquivo, ele substitui o anterior. " + Sounds.UserRules;
        LoadSounds(Sounds.Resolve(App.Store.Settings.ReminderSound));
        VolumeSlider.Value = App.Store.Settings.ReminderVolume;
        Closed += (_, _) => Sounds.StopPreview();
    }

    void LoadSounds(string selectId)
    {
        var sounds = Sounds.All();
        SoundBox.ItemsSource = sounds;
        SoundBox.SelectedItem = sounds.FirstOrDefault(s => s.Id == selectId) ?? sounds.First(s => s.Id == Sounds.DefaultId);

        bool has = Sounds.HasUserSound;
        UserSoundText.Text = has ? sounds.First(s => s.IsUser).Name.TrimStart('★', ' ') : "Nenhum som escolhido.";
        RemoveUserButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    SoundItem? Selected => SoundBox.SelectedItem as SoundItem;

    void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        VolumeText.Text = $"{VolumeSlider.Value:0}%";

    void Preview_Click(object sender, RoutedEventArgs e) =>
        Sounds.Preview(Selected?.Id, (int)VolumeSlider.Value);

    void ChooseUser_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Escolha o seu som (" + Sounds.UserRules + ")",
            Filter = "Som WAV (*.wav)|*.wav",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Sounds.StopPreview();
            Sounds.SetUserSound(dlg.FileName);
            LoadSounds(Sounds.UserId);
            Sounds.Preview(Sounds.UserId, (int)VolumeSlider.Value);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "MyPIN — som não aceito", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void RemoveUser_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Remover o seu som?\nOs lembretes que usam esse som passam a usar o som padrão.",
                "MyPIN", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            Sounds.StopPreview();
            Sounds.RemoveUserSound();
            LoadSounds(Sounds.Resolve(App.Store.Settings.ReminderSound));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não foi possível remover:\n" + ex.Message, "MyPIN", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        App.Store.Settings.ReminderSound = Selected?.Id ?? Sounds.DefaultId;
        App.Store.Settings.ReminderVolume = (int)Math.Round(VolumeSlider.Value);
        App.Store.MarkDirty();
        DialogResult = true;
    }
}
