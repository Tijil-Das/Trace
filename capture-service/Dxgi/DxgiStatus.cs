namespace ScreenRecall.CaptureService.Dxgi;

/// <summary>
/// HRESULT values DXGI returns that the duplication loop must distinguish. SharpGen's own
/// <c>Result.WaitTimeout</c> is the Win32 WAIT_TIMEOUT (258), not DXGI's, so the raw codes are checked
/// explicitly: mistaking a benign timeout for a lost session would rebuild duplication on every idle
/// poll and force a full-screen rescan each time.
/// </summary>
internal static class DxgiStatus
{
    /// <summary>DXGI_ERROR_WAIT_TIMEOUT — no new frame was ready.</summary>
    internal const int WaitTimeout = unchecked((int)0x887A0027);

    /// <summary>DXGI_ERROR_ACCESS_LOST — the session must be rebuilt (mode change, session switch, …).</summary>
    internal const int AccessLost = unchecked((int)0x887A0026);

    /// <summary>DXGI_ERROR_INVALID_CALL — the duplication state is stale; rebuild.</summary>
    internal const int InvalidCall = unchecked((int)0x887A0001);

    /// <summary>DXGI_ERROR_NOT_FOUND — nothing to enumerate.</summary>
    internal const int NotFound = unchecked((int)0x887A0002);

    /// <summary>E_ACCESSDENIED — no access to the current desktop image (see Classify).</summary>
    internal const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>E_INVALIDARG — for DuplicateOutput with a valid device this means "already duplicating".</summary>
    internal const int InvalidArgument = unchecked((int)0x80070057);

    /// <summary>DXGI_ERROR_UNSUPPORTED — the desktop mode or scenario cannot be duplicated.</summary>
    internal const int Unsupported = unchecked((int)0x887A0004);

    /// <summary>DXGI_ERROR_DEVICE_REMOVED — adapter removed or driver upgraded.</summary>
    internal const int DeviceRemoved = unchecked((int)0x887A0005);

    /// <summary>DXGI_ERROR_DEVICE_RESET — the device failed and must be recreated.</summary>
    internal const int DeviceReset = unchecked((int)0x887A0007);

    /// <summary>DXGI_ERROR_NOT_CURRENTLY_AVAILABLE — transient, or the four-app duplication limit.</summary>
    internal const int NotCurrentlyAvailable = unchecked((int)0x887A0022);

    /// <summary>DXGI_ERROR_SESSION_DISCONNECTED — a Remote Desktop session is disconnected.</summary>
    internal const int SessionDisconnected = unchecked((int)0x887A0028);

    /// <summary>DXGI_ERROR_ACCESS_DENIED — a different code from E_ACCESSDENIED (resource privileges).</summary>
    internal const int DxgiAccessDenied = unchecked((int)0x887A002B);

    /// <summary>True when the result means "nothing changed yet".</summary>
    internal static bool IsTimeout(SharpGen.Runtime.Result result)
        => result.Code == WaitTimeout || result == SharpGen.Runtime.Result.WaitTimeout;

    /// <summary>True when the result means the duplication session is no longer usable.</summary>
    internal static bool RequiresRebuild(SharpGen.Runtime.Result result)
        => result.Failure && !IsTimeout(result);

    /// <summary>
    /// A failed attempt to create or keep a duplication session, carrying the classified reason the retry policy
    /// needs along with the raw detail for the log.
    /// </summary>
    internal readonly record struct Failure(UnavailableReason Reason, string Detail);


    /// <summary>
    /// Why DXGI could not hand over a desktop image. The distinction that matters: some of these mean "the
    /// desktop is not visible to this process right now, and that is correct behaviour", and must not be
    /// reported as a fault; others are genuine driver or API failures that belong in the error line. Neither is
    /// ever papered over with invented frames.
    /// </summary>
    internal enum UnavailableReason
    {
        /// <summary>Capture is working.</summary>
        None = 0,

        /// <summary>
        /// E_ACCESSDENIED from DuplicateOutput: "the application does not have access privilege to the current
        /// desktop image. For example, only an application that runs at LOCAL_SYSTEM can access the secure
        /// desktop." A locked workstation, a UAC prompt, or a session that is not the active one. Windows
        /// blocks this on purpose (spec 13); it is temporary and it is not an error to report.
        /// </summary>
        SessionLocked,

        /// <summary>DXGI_ERROR_SESSION_DISCONNECTED: a Remote Desktop session is disconnected (spec 13).</summary>
        SessionDisconnected,

        /// <summary>
        /// DXGI_ERROR_ACCESS_LOST: the duplication interface became invalid and has to be re-created - a desktop
        /// switch, a mode change, DWM being turned on or off, or a full-screen application taking over. Routine,
        /// self-correcting, and expected on any machine where the user does anything (spec 5.1).
        /// </summary>
        SessionLost,

        /// <summary>
        /// DXGI_ERROR_UNSUPPORTED: this desktop mode or scenario cannot be duplicated (8bpp, non-DWM, some
        /// exclusive-fullscreen apps). Microsoft's guidance is to wait for a desktop switch or mode change and
        /// try again, which is what the retry loop does.
        /// </summary>
        ModeUnsupported,

        /// <summary>
        /// DXGI_ERROR_NOT_CURRENTLY_AVAILABLE: DXGI's limit of four concurrent duplication applications is
        /// reached, or the resource is momentarily unavailable. Transient either way.
        /// </summary>
        DuplicationLimit,

        /// <summary>
        /// E_INVALIDARG from DuplicateOutput when the device is valid: this process is already duplicating the
        /// output - in practice, a second recorder running against the same display.
        /// </summary>
        AlreadyDuplicated,

        /// <summary>DXGI_ERROR_DEVICE_REMOVED / DEVICE_RESET: the adapter was removed or the driver reset.</summary>
        DeviceLost,

        /// <summary>Enumeration found no attached, duplicatable output at all.</summary>
        NoOutput,

        /// <summary>Anything else, including failures that are not HRESULTs at all.</summary>
        Unknown,
    }

    /// <summary>
    /// Maps an HRESULT onto the reason the capture loop has to react to. Checked explicitly because the two
    /// "access denied" values are different things: <c>E_ACCESSDENIED</c> (0x80070005) is the secure-desktop
    /// case, while <c>DXGI_ERROR_ACCESS_DENIED</c> (0x887A002B) is about resource privileges.
    /// </summary>
    internal static UnavailableReason Classify(int code) => code switch
    {
        AccessDenied => UnavailableReason.SessionLocked,
        SessionDisconnected => UnavailableReason.SessionDisconnected,
        AccessLost => UnavailableReason.SessionLost,
        Unsupported => UnavailableReason.ModeUnsupported,
        NotCurrentlyAvailable => UnavailableReason.DuplicationLimit,
        InvalidArgument => UnavailableReason.AlreadyDuplicated,
        DeviceRemoved or DeviceReset => UnavailableReason.DeviceLost,
        _ => UnavailableReason.Unknown,
    };

    /// <summary>Classifies a SharpGen result; a success has no reason.</summary>
    internal static UnavailableReason Classify(SharpGen.Runtime.Result result)
        => result.Success ? UnavailableReason.None : Classify(result.Code);

    /// <summary>
    /// True for reasons that are expected on a normal machine and clear on their own: locked or disconnected
    /// session, an unduplicatable mode, the duplication limit, or a second instance already capturing. These
    /// belong on the state line rather than the error line — reporting a locked workstation as a capture error
    /// would train the user to ignore real ones.
    /// </summary>
    internal static bool IsExpected(UnavailableReason reason) => reason is
        UnavailableReason.SessionLocked
        or UnavailableReason.SessionDisconnected
        or UnavailableReason.SessionLost
        or UnavailableReason.ModeUnsupported
        or UnavailableReason.DuplicationLimit
        or UnavailableReason.AlreadyDuplicated
        or UnavailableReason.NoOutput;

    /// <summary>Short phrase for the dashboard and the log.</summary>
    internal static string Describe(UnavailableReason reason) => reason switch
    {
        UnavailableReason.None => "capturing",
        UnavailableReason.SessionLocked => "the session is locked or a secure desktop is showing",
        UnavailableReason.SessionDisconnected => "the Remote Desktop session is disconnected",
        UnavailableReason.SessionLost => "the display session changed and is being re-created",
        UnavailableReason.ModeUnsupported => "the current desktop mode cannot be duplicated",
        UnavailableReason.DuplicationLimit => "another application is already duplicating this display",
        UnavailableReason.AlreadyDuplicated => "this process is already duplicating the display",
        UnavailableReason.DeviceLost => "the display adapter was reset or removed",
        UnavailableReason.NoOutput => "no capturable display output was found",
        _ => "desktop duplication failed",
    };
}
