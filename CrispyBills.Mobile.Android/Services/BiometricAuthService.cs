using Microsoft.Maui.ApplicationModel;
using System.Runtime.Versioning;

#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Biometrics;
using Android.OS;
#endif

namespace CrispyBills.Mobile.Android.Services;

public sealed class BiometricAuthService
{
    public Task<bool> IsAvailableAsync()
    {
#if ANDROID
        if (Platform.CurrentActivity is not Activity activity)
        {
            return Task.FromResult(false);
        }

        if (!OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            return Task.FromResult(false);
        }

        var packageManager = activity.PackageManager;
        var supported = packageManager is not null
            && (packageManager.HasSystemFeature(PackageManager.FeatureFingerprint)
                || (OperatingSystem.IsAndroidVersionAtLeast(29)
                    && (packageManager.HasSystemFeature(PackageManager.FeatureFace)
                        || packageManager.HasSystemFeature(PackageManager.FeatureIris))));

        return Task.FromResult(supported);
#else
        return Task.FromResult(false);
#endif
    }

    public async Task<bool> AuthenticateAsync(string title, string subtitle)
    {
#if ANDROID
        if (Platform.CurrentActivity is not Activity activity || !OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            return false;
        }

        var available = await IsAvailableAsync();
        if (!available)
        {
            return false;
        }

        var executor = activity.MainExecutor;
        if (executor is null)
        {
            return false;
        }

        var tcs = new TaskCompletionSource<bool>();
        var prompt = new BiometricPrompt.Builder(activity)
            .SetTitle(title)
            .SetSubtitle(subtitle)
            .SetNegativeButton("Use PIN", executor, new NegativeButtonListener(tcs))
            .Build();

        prompt.Authenticate(new CancellationSignal(), executor, new PromptCallback(tcs));
        return await tcs.Task;
#else
        return false;
#endif
    }

#if ANDROID
    [SupportedOSPlatform("android28.0")]
    private sealed class PromptCallback(TaskCompletionSource<bool> tcs) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result)
        {
            base.OnAuthenticationSucceeded(result);
            tcs.TrySetResult(true);
        }

        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString)
        {
            base.OnAuthenticationError(errorCode, errString);
            tcs.TrySetResult(false);
        }

        public override void OnAuthenticationFailed()
        {
            base.OnAuthenticationFailed();
        }
    }

    private sealed class NegativeButtonListener(TaskCompletionSource<bool> tcs) : Java.Lang.Object, IDialogInterfaceOnClickListener
    {
        public void OnClick(IDialogInterface? dialog, int which)
        {
            tcs.TrySetResult(false);
        }
    }
#endif
}
