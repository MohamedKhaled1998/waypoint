import { useTranslation } from 'react-i18next';
import { EmptyState } from '../../ui/Screen';

export default function CoachScreen() {
  const { t } = useTranslation();
  return <EmptyState title={t('coach.emptyTitle')} body={t('coach.emptyBody')} />;
}
