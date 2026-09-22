using ScreenRecall.CaptureService.Dxgi;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>
/// Which DXGI failures mean "the desktop is not visible right now, and that is correct" and which mean "something
/// is broken" decides whether the service idles quietly or reports a fault - and, crucially, whether it is allowed
/// to fall back to recording something else. Getting it wrong either spams the user or hides a real problem, so the
/// documented HRESULTs are pinned here (spec 5.1 / 13).
/// </summary>
public sealed class DxgiFailureTests
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int Unsupported = unchecked((int)0x887A0004);
    private const int DeviceRemoved = unchecked((int)0x887A0005);
    private const int NotCurrentlyAvailable = unchecked((int)0x887A0022);
    private const int AccessLost = unchecked((int)0x887A0026);
    private const int SessionDisconnected = unchecked((int)0x887A0028);
    private const int DxgiAccessDenied = unchecked((int)0x887A002B);

    [Fact]
    public void ALockedWorkstationIsExpectedRatherThanAnError()
    {
        // E_ACCESSDENIED from DuplicateOutput is documented as "the application does not have access privilege to
        // the current desktop image... only an application that runs at LOCAL_SYSTEM can access the secure
        // desktop": a lock screen, a UAC prompt, or a session that is not the active one.
        Assert.Equal(DxgiStatus.UnavailableReason.SessionLocked, DxgiStatus.Classify(AccessDenied));
        Assert.True(DxgiStatus.IsExpected(DxgiStatus.UnavailableReason.SessionLocked));
    }

    [Fact]
    public void TheTwoAccessDeniedCodesMeanDifferentThings()
    {
        // 0x80070005 is the secure desktop; 0x887A002B is about resource privileges. Treating them as one would
        // hide a real fault behind "the session is locked".
        Assert.Equal(DxgiStatus.UnavailableReason.SessionLocked, DxgiStatus.Classify(AccessDenied));
        Assert.NotEqual(DxgiStatus.UnavailableReason.SessionLocked, DxgiStatus.Classify(DxgiAccessDenied));
    }

    [Fact]
    public void ADesktopSwitchIsRoutineAndADeviceFaultIsNot()
    {
        Assert.Equal(DxgiStatus.UnavailableReason.SessionLost, DxgiStatus.Classify(AccessLost));
        Assert.True(DxgiStatus.IsExpected(DxgiStatus.UnavailableReason.SessionLost));

        Assert.Equal(DxgiStatus.UnavailableReason.DeviceLost, DxgiStatus.Classify(DeviceRemoved));
        Assert.False(DxgiStatus.IsExpected(DxgiStatus.UnavailableReason.DeviceLost));

        // Anything unrecognised reaches the user: better a message about something odd than a silent idle.
        Assert.Equal(DxgiStatus.UnavailableReason.Unknown, DxgiStatus.Classify(0x1234));
        Assert.False(DxgiStatus.IsExpected(DxgiStatus.UnavailableReason.Unknown));
    }

    [Fact]
    public void DisconnectedSessionsLimitsAndUnsupportedModesAreAllExpected()
    {
        Assert.Equal(DxgiStatus.UnavailableReason.SessionDisconnected, DxgiStatus.Classify(SessionDisconnected));
        Assert.Equal(DxgiStatus.UnavailableReason.DuplicationLimit, DxgiStatus.Classify(NotCurrentlyAvailable));
        Assert.Equal(DxgiStatus.UnavailableReason.ModeUnsupported, DxgiStatus.Classify(Unsupported));
        Assert.Equal(DxgiStatus.UnavailableReason.AlreadyDuplicated, DxgiStatus.Classify(InvalidArgument));

        foreach (DxgiStatus.UnavailableReason reason in new[]
                 {
                     DxgiStatus.UnavailableReason.SessionDisconnected,
                     DxgiStatus.UnavailableReason.DuplicationLimit,
                     DxgiStatus.UnavailableReason.ModeUnsupported,
                     DxgiStatus.UnavailableReason.AlreadyDuplicated,
                     DxgiStatus.UnavailableReason.NoOutput,
                 })
        {
            Assert.True(DxgiStatus.IsExpected(reason), $"{reason} should be treated as expected");
        }
    }

    [Fact]
    public void EveryReasonHasSomethingToShowTheUser()
    {
        foreach (DxgiStatus.UnavailableReason reason in Enum.GetValues<DxgiStatus.UnavailableReason>())
        {
            Assert.False(string.IsNullOrWhiteSpace(DxgiStatus.Describe(reason)));
        }
    }
}
