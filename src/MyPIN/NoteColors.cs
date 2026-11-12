using System.Windows.Media;

namespace MyPIN;

public sealed class NoteColor
{
    public NoteColor(string key, string name, string body, string header)
    {
        Key = key;
        Name = name;
        BodyBrush = MakeBrush(body);
        HeaderBrush = MakeBrush(header);
    }

    public string Key { get; }
    public string Name { get; }
    public SolidColorBrush BodyBrush { get; }
    public SolidColorBrush HeaderBrush { get; }

    static SolidColorBrush MakeBrush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

public static class NoteColors
{
    public static readonly NoteColor[] All =
    {
        new("Yellow", "Amarelo", "#FFF6A9", "#F7E36A"),
        new("Green",  "Verde",   "#D4F5C4", "#A9E18E"),
        new("Pink",   "Rosa",    "#FFD6E7", "#F7A8C8"),
        new("Blue",   "Azul",    "#D3EAFF", "#9CCBF7"),
        new("Purple", "Roxo",    "#E6DAFF", "#C5ADF5"),
        new("Orange", "Laranja", "#FFE0BD", "#F7BB79"),
        new("Mint",   "Menta",   "#C9F2EC", "#8FDCCF"),
        new("Gray",   "Cinza",   "#EFEFEF", "#D2D2D2"),
        new("White",  "Branco",  "#FFFFFF", "#E6E6E6"),
    };

    public static NoteColor Get(string? key) => All.FirstOrDefault(c => c.Key == key) ?? All[0];
}
