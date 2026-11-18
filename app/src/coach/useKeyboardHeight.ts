import { useEffect, useState } from 'react';
import { Keyboard } from 'react-native';

/** The measured keyboard height (0 when hidden). Edge-to-edge Android doesn't resize the window, so we pad by it. */
export function useKeyboardHeight() {
  const [height, setHeight] = useState(0);
  useEffect(() => {
    const show = Keyboard.addListener('keyboardDidShow', (e) => setHeight(e.endCoordinates.height));
    const hide = Keyboard.addListener('keyboardDidHide', () => setHeight(0));
    return () => {
      show.remove();
      hide.remove();
    };
  }, []);
  return height;
}
