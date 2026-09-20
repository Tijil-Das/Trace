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

    /// <summary>True when the result means "nothing changed yet".</summary>
    internal static bool IsTimeout(SharpGen.Runtime.Result result)
        => result.Code == WaitTimeout || result == SharpGen.Runtime.Result.WaitTimeout;

    /// <summary>True when the result means the duplication session is no longer usable.</summary>
    internal static bool RequiresRebuild(SharpGen.Runtime.Result result)
        => result.Failure && !IsTimeout(result);
}
