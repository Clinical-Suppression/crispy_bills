using CrispyBills.Core.Security;
using Xunit;

namespace CrispyBills.Mobile.ParityTests;

public sealed class AppLockTimeoutPolicyTests
{
    [Fact]
    public void ShouldDismissModalOnTimeout_Unlock_ReturnsFalse()
    {
        Assert.False(AppLockTimeoutPolicy.ShouldDismissModalOnTimeout(AppLockFlowStep.Unlock));
    }

    [Fact]
    public void ShouldDismissModalOnTimeout_PinSetup_ReturnsTrue()
    {
        Assert.True(AppLockTimeoutPolicy.ShouldDismissModalOnTimeout(AppLockFlowStep.PinSetup));
    }
}
