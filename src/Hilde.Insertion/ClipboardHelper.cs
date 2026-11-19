using System.Runtime.InteropServices;
using System.Windows;

namespace Hilde.Insertion;

/// <summary>
/// Zwischenablage sichern, Diktat setzen (ohne Eintrag im Windows-Zwischenablageverlauf) und
/// den ursprünglichen Inhalt nur dann zurückschreiben, wenn sich zwischenzeitlich nichts geändert hat.
/// </summary>
internal sealed class ClipboardSnapshot
{
    private readonly List<(string Format, object Data)> _items = [];
    public bool Restorable { get; private set; } = true;
    public bool WasEmpty => _items.Count == 0;

    public static ClipboardSnapshot Capture()
    {
        var snap = new ClipboardSnapshot();
        IDataObject? data = Retry(() => Clipboard.GetDataObject());
        if (data == null) return snap;
        foreach (var format in data.GetFormats(autoConvert: false))
        {
            try
            {
                var value = data.GetData(format, autoConvert: false);
                if (value != null) snap._items.Add((format, value));
            }
            catch (Exception)
            {
                // Inhalt nicht vollständig sicherbar → lieber nicht zurückschreiben
                snap.Restorable = false;
            }
        }
        return snap;
    }

    public void Restore()
    {
        if (!Restorable) return;
        if (_items.Count == 0) { Retry(() => { Clipboard.Clear(); return true; }); return; }
        var obj = new DataObject();
        foreach (var (format, data) in _items)
        {
            try { obj.SetData(format, data); } catch { /* Format nicht übertragbar */ }
        }
        Retry(() => { Clipboard.SetDataObject(obj, copy: true); return true; });
    }

    public static void SetText(string text)
    {
        var obj = new DataObject();
        obj.SetData(DataFormats.UnicodeText, text);
        obj.SetData(DataFormats.Text, text);
        // Nicht im Windows-Zwischenablageverlauf und nicht in der Cloud-Zwischenablage speichern
        obj.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(0)));
        obj.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        obj.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        Retry(() => { Clipboard.SetDataObject(obj, copy: true); return true; });
    }

    /// <summary>Andere Programme halten die Zwischenablage oft kurz offen – mehrfach versuchen.</summary>
    public static T Retry<T>(Func<T> action, int attempts = 8)
    {
        for (int i = 1; ; i++)
        {
            try { return action(); }
            catch (COMException) when (i < attempts) { Thread.Sleep(40 * i); }
            catch (ExternalException) when (i < attempts) { Thread.Sleep(40 * i); }
        }
    }
}
