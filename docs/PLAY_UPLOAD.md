# Google Play Upload Setup (Prepared, Optional)

This repository includes a manual workflow for Google Play upload:

- [ .github/workflows/play-upload.yml ](.github/workflows/play-upload.yml)

It is safe by default:

- It always builds a signed .aab artifact.
- It uploads to Play only if you explicitly set `perform_upload=true` in workflow inputs.

## 1. Prerequisites

1. Android signing secrets are already configured:
1. ANDROID_KEYSTORE_BASE64
1. ANDROID_KEYSTORE_PASSWORD
1. ANDROID_KEY_ALIAS
1. ANDROID_KEY_PASSWORD

1. You have a Google Play Console app created with package name:
1. `com.crispybills.mobile.android` (or your chosen package)

## 2. Create Google Service Account For Play API

1. In Google Cloud Console, create a service account.
1. Create and download a JSON key for it.
1. In Play Console:
1. Go to Setup > API access.
1. Link the Google Cloud project.
1. Grant the service account app permissions (at minimum release management for your target track).

## 3. Add GitHub Secret

Add this repository secret with the raw JSON content (not base64):

- GOOGLE_PLAY_SERVICE_ACCOUNT_JSON

## 4. Run The Workflow

1. Open GitHub Actions.
1. Run workflow: Google Play Upload (Manual).
1. Choose inputs:
1. perform_upload=false to test build/sign only (no Play upload).
1. perform_upload=true when you are ready to send to Play.
1. package_name to match your app ID.
1. track: internal, alpha, beta, or production.
1. release_status: completed, draft, inProgress, or halted.

## 5. Recommended First Dry Run

1. Run once with `perform_upload=false`.
1. Download artifact `crispybills-signed-aab` from the workflow run.
1. Confirm bundle metadata/version is correct.
1. Re-run with `perform_upload=true` and track `internal`.

## 6. Version Rules For Play

Before each Play upload, update these in [ CrispyBills.Mobile.Android/CrispyBills.Mobile.Android.csproj ](CrispyBills.Mobile.Android/CrispyBills.Mobile.Android.csproj):

1. ApplicationDisplayVersion (user-facing)
1. ApplicationVersion (integer, must strictly increase each upload)

This repository's Play upload workflow now validates that `ApplicationVersion` is higher than the most recent `v*` tag's version code before upload continues.
