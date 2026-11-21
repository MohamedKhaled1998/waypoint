import { GoogleSignin, isSuccessResponse } from '@react-native-google-signin/google-signin';

let configured = false;

/** The module refuses every call until it is configured, including the sign-out of an app opened already signed in. */
function ensureConfigured() {
  if (configured) return;
  GoogleSignin.configure({ webClientId: process.env.EXPO_PUBLIC_GOOGLE_WEB_CLIENT_ID });
  configured = true;
}

/** Asks Android Credential Manager for a Google ID token; null when the user cancels. */
export async function getGoogleIdToken(): Promise<string | null> {
  ensureConfigured();
  await GoogleSignin.hasPlayServices();
  const response = await GoogleSignin.signIn();
  return isSuccessResponse(response) ? response.data.idToken : null;
}

/** Forgets the Google account, so the next sign-in shows the account picker. */
export async function clearGoogleAccount() {
  ensureConfigured();
  try {
    await GoogleSignin.signOut();
  } catch {
    // Nothing to clear.
  }
}
