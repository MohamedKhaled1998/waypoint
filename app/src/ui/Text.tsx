import { Text as RNText, type TextProps } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';

type Variant = 'title' | 'heading' | 'body' | 'label' | 'caption';

export function Text({ variant = 'body', muted, style, ...rest }: TextProps & { variant?: Variant; muted?: boolean }) {
  styles.useVariants({ variant, muted: muted ? 'yes' : 'no' });
  return <RNText {...rest} style={[styles.text, style]} />;
}

const styles = StyleSheet.create((theme) => ({
  text: {
    color: theme.colors.text,
    variants: {
      variant: {
        title: theme.type.title,
        heading: theme.type.heading,
        body: theme.type.body,
        label: theme.type.label,
        caption: theme.type.caption,
      },
      muted: { yes: { color: theme.colors.textMuted }, no: {} },
    },
  },
}));
