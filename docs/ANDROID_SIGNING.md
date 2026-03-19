# Android Signing And Distribution

This repo can build Android in two modes in GitHub Actions:

- Unsigned APK (if signing secrets are not configured)
- Signed APK + signed AAB (if signing secrets are configured)

The workflow file is [ .github/workflows/release-build.yml ](.github/workflows/release-build.yml).

## 1. Create A Release Keystore

Run once on your machine:

```powershell
keytool -genkeypair -v -keystore crispybills-release.keystore -alias crispybills -keyalg RSA -keysize 2048 -validity 10000
```

Keep this file safe. Losing it can block updates to the same Play Store app identity.

## 2. Convert Keystore To Base64

From the folder where the keystore exists:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes(".\crispybills-release.keystore")) | Set-Clipboard
```

## 3. Add GitHub Repository Secrets

In GitHub repository settings, add these secrets:

- ANDROID_KEYSTORE_BASE64: base64 content of the keystore file
- ANDROID_KEYSTORE_PASSWORD: keystore password
- ANDROID_KEY_ALIAS: key alias (example: crispybills)
- ANDROID_KEY_PASSWORD: key password

## 4. Trigger A Release Build

- Push a tag like v1.0.0
- Or run the workflow manually

Tag builds publish artifacts to a GitHub Release automatically.

## 5. Use APK vs AAB

- APK: direct install/testing and GitHub downloads
- AAB: Google Play Store upload format

For Play Store distribution, upload the signed .aab output from workflow artifacts or the GitHub Release assets.

## 6. Versioning For Store Releases

Before Play upload, update Android app version fields in [ CrispyBills.Mobile.Android/CrispyBills.Mobile.Android.csproj ](CrispyBills.Mobile.Android/CrispyBills.Mobile.Android.csproj):

- ApplicationDisplayVersion (user-facing version, example: 1.2.0)
- ApplicationVersion (monotonic integer, must increase each upload)
