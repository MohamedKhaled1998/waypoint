import * as SecureStore from 'expo-secure-store';
import type { SecureStorage } from './session';

/** Android Keystore-backed storage. */
export const keystoreStorage: SecureStorage = {
  get: (key) => SecureStore.getItemAsync(key),
  set: (key, value) => SecureStore.setItemAsync(key, value),
  remove: (key) => SecureStore.deleteItemAsync(key),
};
