using System.Windows.Media;

namespace Folio.App;

/// <summary>Latin text uses Consolas; Chinese characters and punctuation use the installed FangSong face.</summary>
public static class AppTypography
{
    public static FontFamily Text { get; } = CreateTextFamily();

    private static FontFamily CreateTextFamily()
    {
        var family = new FontFamily();
        family.FamilyMaps.Add(new FontFamilyMap
        {
            Unicode = "2E80-2FFF,3000-303F,3100-312F,31A0-31BF,31C0-31EF,3200-33FF,3400-4DBF,4E00-9FFF,F900-FAFF,FE10-FE1F,FE30-FE4F,FF00-FFEF,20000-2FA1F,30000-323AF",
            Target = "FangSong"
        });
        family.FamilyMaps.Add(new FontFamilyMap { Target = "Consolas" });
        return family;
    }
}
