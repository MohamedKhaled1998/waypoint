/**
 * A stand-in for the Google Sign-In module that behaves like its Android side: nothing works until `configure`, and it
 * remembers the last account until `signOut`, so a sign-in after that skips the account picker.
 */
const google = { configured: false, rememberedAccount: null as string | null };

jest.mock('@react-native-google-signin/google-signin', () => ({
  GoogleSignin: {
    configure: () => void (google.configured = true),
    hasPlayServices: async () => true,
    signIn: async () => {
      if (!google.configured) throw new Error('apiClient is null - call configure first');
      google.rememberedAccount = 'picked-account';
      return { type: 'success', data: { idToken: 'id-token' } };
    },
    signOut: async () => {
      if (!google.configured) throw new Error('apiClient is null - call configure first');
      google.rememberedAccount = null;
    },
  },
  isSuccessResponse: (r: { type: string }) => r.type === 'success',
}));

/** The module as a fresh app launch loads it. */
function launch(): typeof import('./google') {
  let m!: typeof import('./google');
  jest.isolateModules(() => {
    m = require('./google');
  });
  return m;
}

beforeEach(() => {
  google.configured = false;
  google.rememberedAccount = null;
});

describe('signing out of Google', () => {
  it('forgets the account even when the app was opened already signed in, so the next sign-in shows the picker', async () => {
    google.rememberedAccount = 'last-account'; // signed in before this launch; nothing has configured the module yet
    const { clearGoogleAccount } = launch();
    await clearGoogleAccount();
    expect(google.rememberedAccount).toBeNull();
  });

  it('forgets the account picked in this launch', async () => {
    const { clearGoogleAccount, getGoogleIdToken } = launch();
    await getGoogleIdToken();
    await clearGoogleAccount();
    expect(google.rememberedAccount).toBeNull();
  });
});
