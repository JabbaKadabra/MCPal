import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import { CopyButton } from '../components/CopyButton';
import { ErrorText } from '../components/ErrorText';
import { t } from '../i18n';

function Snippet({ value }: { value: string }) {
  return (
    <div className="row snippet">
      <code>{value}</code>
      <CopyButton value={value} />
    </div>
  );
}

export function ConnectPage() {
  const info = useQuery({ queryKey: ['connect-info'], queryFn: api.connectInfo });

  return (
    <>
      <h1>{t('connect.title')}</h1>
      <ErrorText error={info.error} />
      {info.data !== undefined && (
        <>
          <ol>
            <li>{t('connect.step1')}</li>
            <li>
              {t('connect.step2')}
              <Snippet value={info.data.mcpUrl} />
            </li>
            <li>{t('connect.step3')}</li>
          </ol>
          <h2>{t('connect.header.title')}</h2>
          <p>{t('connect.header.body')}</p>
          <h2>{t('connect.code.title')}</h2>
          <p>{t('connect.code.body')}</p>
          <Snippet value={info.data.claudeCodeCommand} />
          <Snippet value={info.data.claudeCodeCommandWithHeader} />
        </>
      )}
    </>
  );
}
