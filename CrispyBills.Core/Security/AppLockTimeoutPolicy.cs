namespace CrispyBills.Core.Security;

public enum AppLockFlowStep
{
    Unlock,
    PinSetup
}

public static class AppLockTimeoutPolicy
{
    public static bool ShouldDismissModalOnTimeout(AppLockFlowStep step)
    {
        // Keep unlock flow fail-closed. Allow dismiss for optional setup prompt.
        return step == AppLockFlowStep.PinSetup;
    }
}
