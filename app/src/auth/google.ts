import { GoogleSignin, isSuccessResponse } from '@react-native-google-signin/google-signin';

let configured = false;

/** Asks Android Credential Manager for a Google ID token; null when the user cancels. */
export async function getGoogleIdToken(): Promise<string | null> {
  if (!configured) {
    GoogleSignin.configure({ webClientId: process.env.EXPO_PUBLIC_GOOGLE_WEB_CLIENT_ID });
    configured = true;
  }
  await GoogleSignin.hasPlayServices();
  const response = await GoogleSignin.signIn();
  return isSuccessResponse(response) ? response.data.idToken : null;
}

export async function clearGoogleAccount() {
  try {
    await GoogleSignin.signOut();
  } catch {
    // Nothing to clear.
  }
}
