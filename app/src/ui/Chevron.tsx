import Ionicons from '@expo/vector-icons/Ionicons';
import { I18nManager } from 'react-native';
import { useUnistyles } from 'react-native-unistyles';

/** Points to the end side, so it turns round in right-to-left layouts. */
export function Chevron() {
  const { theme } = useUnistyles();
  return <Ionicons name={I18nManager.isRTL ? 'chevron-back' : 'chevron-forward'} size={18} color={theme.colors.textMuted} />;
}
