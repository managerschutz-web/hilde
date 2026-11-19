using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Hilde.Core;
using Hilde.Insertion;
using Serilog;
using Xunit;

namespace Hilde.Tests.WindowsE2E;

[Trait("Category", "WindowsE2E")]
public class InsertionE2ETests
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private sealed class FixedSettings(HildeSettings s) : ISettingsStore
    {
        public HildeSettings Current { get; } = s;
        public event EventHandler<HildeSettings>? Changed { add { } remove { } }
        public void Save(HildeSettings settings) { }
    }

    [Theory]
    [InlineData(InsertionMethod.Auto)]
    [InlineData(InsertionMethod.Typing)]
    public async Task Inserts_unicode_text_into_notepad(InsertionMethod method)
    {
        var settings = new HildeSettings();
        settings.Insertion.Method = method;
        var service = new Win32TextInsertionService(new FixedSettings(settings), new LoggerConfiguration().CreateLogger());
        using var p = Process.Start("notepad.exe")!;
        try
        {
            p.WaitForInputIdle(5000);
            IntPtr hwnd = IntPtr.Zero;
            for (int i = 0; i < 50 && hwnd == IntPtr.Zero; i++) { await Task.Delay(100); p.Refresh(); hwnd = p.MainWindowHandle; }
            Assert.NotEqual(IntPtr.Zero, hwnd);
            SetForegroundWindow(hwnd);
            await Task.Delay(300);

            var target = service.CaptureTarget();
            const string text = "Hallo HILDE – äöüß € MARTENS & PRAHL";
            var outcome = await service.InsertAsync(target, text, CancellationToken.None);
            Assert.True(outcome.Success, outcome.Detail);
            await Task.Delay(500);

            var root = AutomationElement.FromHandle(hwnd);
            var doc = root.FindFirst(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
            Assert.NotNull(doc);
            string content = doc.TryGetCurrentPattern(TextPattern.Pattern, out var tp)
                ? ((TextPattern)tp).DocumentRange.GetText(-1)
                : ((ValuePattern)doc.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
            Assert.Contains(text, content);
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }

    [Fact]
    public async Task Missing_target_is_reported()
    {
        var service = new Win32TextInsertionService(new FixedSettings(new HildeSettings()), new LoggerConfiguration().CreateLogger());
        using var p = Process.Start("notepad.exe")!;
        p.WaitForInputIdle(5000);
        await Task.Delay(500);
        p.Refresh();
        SetForegroundWindow(p.MainWindowHandle);
        await Task.Delay(300);
        var target = service.CaptureTarget();
        p.Kill(true);
        p.WaitForExit(3000);
        var outcome = await service.InsertAsync(target, "x", CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Equal(HildeErrors.TargetGone.Id, outcome.ErrorId);
    }
}
