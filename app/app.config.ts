import { existsSync } from 'node:fs';
import { join } from 'node:path';
import type { ConfigContext, ExpoConfig } from 'expo/config';

// Firebase's google-services.json is gitignored. Use it only when it is there, so typecheck, tests and CI never need it.
const googleServicesFile = join(__dirname, 'google-services.json');

export default ({ config }: ConfigContext): ExpoConfig => ({
  ...(config as ExpoConfig),
  android: {
    ...config.android,
    ...(existsSync(googleServicesFile) ? { googleServicesFile: './google-services.json' } : {}),
  },
});
