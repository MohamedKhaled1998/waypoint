import { File, Paths } from 'expo-file-system';
import { isAvailableAsync, shareAsync } from 'expo-sharing';

/** Saves the export zip to the cache and opens the phone's share sheet so the user can keep it. */
export async function shareExport(zip: Uint8Array): Promise<boolean> {
  const file = new File(Paths.cache, 'waypoint-export.zip');
  if (file.exists) file.delete();
  file.create();
  file.write(zip);
  if (!(await isAvailableAsync())) return false;
  await shareAsync(file.uri, { mimeType: 'application/zip', dialogTitle: 'Waypoint' });
  return true;
}
