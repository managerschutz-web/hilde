using Hilde.Core;
using Xunit;

namespace Hilde.Tests.Unit;

public class GestureRecognizerTests
{
    private static GestureRecognizer New() => new() { MinHoldMs = 250, DoubleTapMs = 350 };

    [Fact]
    public void Hold_and_release_processes()
    {
        var g = New();
        Assert.Equal(GestureAction.StartProvisional, g.TriggerDown(0));
        Assert.True(g.IsRecording);
        Assert.Equal(GestureAction.StopAndProcess, g.TriggerUp(900));
        Assert.False(g.IsRecording);
    }

    [Fact]
    public void Short_hold_waits_for_second_tap_then_processes()
    {
        var g = New();
        g.TriggerDown(0);
        Assert.Equal(GestureAction.AwaitSecondTap, g.TriggerUp(400));
        Assert.True(g.IsRecording);
        Assert.Equal(GestureAction.None, g.ResolvePending(500));   // Fenster noch offen
        Assert.Equal(GestureAction.StopAndProcess, g.ResolvePending(800));
        Assert.False(g.IsRecording);
    }

    [Fact]
    public void Slow_double_tap_turns_recording_into_hands_free()
    {
        var g = New();
        g.TriggerDown(0);
        Assert.Equal(GestureAction.AwaitSecondTap, g.TriggerUp(400));
        Assert.Equal(GestureAction.StartHandsFree, g.TriggerDown(600));
        Assert.Equal(GestureAction.None, g.TriggerUp(700));
        Assert.True(g.IsHandsFree);
        Assert.Equal(GestureAction.None, g.ResolvePending(1000));
        Assert.Equal(GestureAction.StopAndProcess, g.TriggerDown(4000));
    }

    [Fact]
    public void Escape_cancels_pending_short_hold()
    {
        var g = New();
        g.TriggerDown(0);
        g.TriggerUp(400);
        Assert.Equal(GestureAction.Cancel, g.Escape());
        Assert.Equal(GestureAction.None, g.ResolvePending(900));
    }

    [Fact]
    public void Short_tap_discards()
    {
        var g = New();
        g.TriggerDown(0);
        Assert.Equal(GestureAction.DiscardProvisional, g.TriggerUp(100));
        Assert.False(g.IsRecording);
    }

    [Fact]
    public void Double_tap_starts_hands_free_and_next_press_stops()
    {
        var g = New();
        g.TriggerDown(0);
        g.TriggerUp(100);
        Assert.Equal(GestureAction.StartHandsFree, g.TriggerDown(250));
        Assert.Equal(GestureAction.None, g.TriggerUp(300));
        Assert.True(g.IsHandsFree);
        Assert.Equal(GestureAction.StopAndProcess, g.TriggerDown(5000));
        Assert.Equal(GestureAction.None, g.TriggerUp(5100));
        Assert.False(g.IsRecording);
    }

    [Fact]
    public void Late_second_tap_is_a_new_push_to_talk()
    {
        var g = New();
        g.TriggerDown(0);
        g.TriggerUp(100);
        Assert.Equal(GestureAction.StartProvisional, g.TriggerDown(1000));
    }

    [Fact]
    public void Shortcut_with_other_key_discards()
    {
        var g = New();
        g.TriggerDown(0);
        Assert.Equal(GestureAction.DiscardProvisional, g.OtherKeyDown(50));
        Assert.Equal(GestureAction.None, g.TriggerUp(80));
        Assert.False(g.IsRecording);
    }

    [Fact]
    public void Escape_cancels_push_to_talk()
    {
        var g = New();
        g.TriggerDown(0);
        Assert.Equal(GestureAction.Cancel, g.Escape());
        Assert.Equal(GestureAction.None, g.TriggerUp(400));
    }

    [Fact]
    public void Escape_cancels_hands_free()
    {
        var g = New();
        Assert.Equal(GestureAction.StartHandsFree, g.ExternalToggle());
        Assert.Equal(GestureAction.Cancel, g.Escape());
        Assert.False(g.IsRecording);
        Assert.Equal(GestureAction.None, g.Escape());
    }

    [Fact]
    public void External_toggle_starts_and_stops()
    {
        var g = New();
        Assert.Equal(GestureAction.StartHandsFree, g.ExternalToggle());
        Assert.Equal(GestureAction.StopAndProcess, g.ExternalToggle());
        Assert.False(g.IsRecording);
    }

    [Fact]
    public void Other_keys_while_hands_free_do_not_abort()
    {
        var g = New();
        g.ExternalToggle();
        Assert.Equal(GestureAction.None, g.OtherKeyDown(10));
        Assert.True(g.IsRecording);
    }
}

public class GesturePauseTests
{
    [Fact]
    public void Force_hands_free_keeps_recording_after_release()
    {
        var g = new Hilde.Core.GestureRecognizer();
        g.TriggerDown(0);
        g.ForceHandsFree();
        Assert.Equal(Hilde.Core.GestureAction.None, g.TriggerUp(800));
        Assert.True(g.IsHandsFree);
        Assert.Equal(Hilde.Core.GestureAction.StopAndProcess, g.TriggerDown(5000));
    }
}
