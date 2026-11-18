import i18next from 'i18next';
import { initReactI18next } from 'react-i18next';
import { getLocales } from 'expo-localization';
import { en } from './en';

void i18next.use(initReactI18next).init({
  resources: { en: { translation: en } },
  lng: getLocales()[0]?.languageCode ?? 'en',
  fallbackLng: 'en',
  interpolation: { escapeValue: false },
  returnNull: false,
});

export default i18next;
