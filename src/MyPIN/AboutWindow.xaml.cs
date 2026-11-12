using System.Globalization;
using System.Reflection;
using System.Windows;
using MyPIN.Services;

namespace MyPIN;

/// <summary>Janela "Sobre": ícone, nome, autor, versão e data.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Icon = AppIcon.CreateImageSource();
        Logo.Source = AppIcon.CreateImageSource();
        VersionText.Text = $"Versão {Version} · {BuildDate}";
    }

    /// <summary>Versão do programa (ex.: 1.2.0).</summary>
    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Data em que esta versão foi gerada (dd/MM/aaaa).</summary>
    public static string BuildDate
    {
        get
        {
            var raw = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildDate")?.Value;
            return DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.ToString("dd/MM/yyyy") : "";
        }
    }

    void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
