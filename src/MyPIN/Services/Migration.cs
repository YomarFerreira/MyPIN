using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace MyPIN.Services;

/// <summary>
/// Passagem do nome antigo (MyPostIt) para o novo (MyPIN):
///  • copia as notas e configurações de %AppData%\MyPostIt para %AppData%\MyPIN (a pasta antiga
///    fica intacta, como backup);
///  • troca a entrada "iniciar com o Windows" antiga pela nova.
/// Roda só uma vez (quando a pasta nova ainda não existe) e nunca no modo de teste.
/// </summary>
public static class Migration
{
    const string OldName = "MyPostIt";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static string OldDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldName);

    /// <summary>Devolve false se o programa não deve continuar abrindo.</summary>
    public static bool Run()
    {
        if (NoteStore.IsTestMode) return true;
        MigrateStartup();

        if (Directory.Exists(NoteStore.DataDir) || !Directory.Exists(OldDir)) return true;

        // o programa antigo aberto continuaria gravando na pasta antiga: pede para fechar antes
        if (Process.GetProcessesByName(OldName).Length > 0)
        {
            MessageBox.Show(
                "O programa antigo (MyPostIt) ainda está aberto.\n\n" +
                "Feche-o pelo ícone da bandeja (botão direito → Sair) e abra o MyPIN de novo.\n" +
                "Assim as suas notas são copiadas por completo para o MyPIN.",
                "MyPIN", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        // copia para uma pasta temporária e só no fim dá o nome definitivo:
        // se algo der errado no meio, não fica uma pasta MyPIN pela metade
        var tmp = NoteStore.DataDir + ".copiando";
        try
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
            CopyDir(OldDir, tmp);
            Directory.Move(tmp, NoteStore.DataDir);
            return true;
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { }
            // NÃO abre com a pasta vazia (pareceria que as notas sumiram)
            MessageBox.Show(
                "Não foi possível copiar as suas notas da pasta antiga:\n" + OldDir + "\n\n" + ex.Message +
                "\n\nNada foi apagado. Tente abrir o MyPIN de novo.",
                "MyPIN", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from))
            CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>Se o MyPostIt estava para iniciar com o Windows, passa a iniciar o MyPIN.</summary>
    static void MigrateStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(OldName) is not string) return;
            key.DeleteValue(OldName, throwOnMissingValue: false);
            StartupManager.Set(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }
}
