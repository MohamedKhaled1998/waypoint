import { useTranslation } from 'react-i18next';
import { EmptyState } from '../../ui/Screen';

export default function ProgressScreen() {
  const { t } = useTranslation();
  return <EmptyState title={t('progress.emptyTitle')} body={t('progress.emptyBody')} />;
}
