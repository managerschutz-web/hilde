namespace Hilde.Desktop;

/// <summary>Erlaubte Diktiertasten und deutsche Namen. Buchstaben/Ziffern/AltGr sind ausgeschlossen.</summary>
internal static class HotkeyNames
{
    public static bool IsAllowed(int vk) =>
        vk is 0xA3          // Strg rechts
            or 0xA1         // Umschalt rechts
            or 0x13         // Pause
            or 0x91         // Rollen
            or 0x5D         // Kontextmenü-Taste
            or 0x2D         // Einfg
            or >= 0x70 and <= 0x87; // F1–F24

    public static string Name(int vk) => vk switch
    {
        0xA3 => "Strg rechts",
        0xA2 => "Strg links",
        0xA1 => "Umschalt rechts",
        0xA0 => "Umschalt links",
        0xA5 => "Alt Gr",
        0xA4 => "Alt",
        0x13 => "Pause",
        0x91 => "Rollen",
        0x5D => "Kontextmenü",
        0x2D => "Einfg",
        0x1B => "Esc",
        0x0D => "Eingabe",
        0x20 => "Leertaste",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => $"Taste 0x{vk:X2}"
    };
}
