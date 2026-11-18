import { useTranslation } from 'react-i18next';
import { EmptyState } from '../../ui/Screen';

export default function TodayScreen() {
  const { t } = useTranslation();
  return <EmptyState title={t('today.emptyTitle')} body={t('today.emptyBody')} />;
}
