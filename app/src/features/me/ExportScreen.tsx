// Me > Export my data: start the export, watch it move through queued, running and ready (or failed), then download it.
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { startExport, useGetExport } from '@/api/generated/account/account';
import { isWeb } from '@/lib/auth/session';
import { friendlyError } from '@/lib/problem';
import { Button, Card, LoadingState, Screen, Text } from '@/ui';
import { exportPoll, getSharer } from './services';

const progress: Record<string, string> = {
  queued: 'Your export is waiting to start.',
  running: 'Putting your library into files. This can take a minute.',
};

export function ExportScreen() {
  const [jobId, setJobId] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const start = useMutation({
    mutationFn: () => startExport(),
    onSuccess: (response) => {
      if (response.status === 202) setJobId(response.data.jobId);
    },
    onError: (e) => setMessage(friendlyError(e)),
  });

  const job = useGetExport(jobId ?? '', {
    query: {
      enabled: !!jobId,
      refetchInterval: (query) => {
        const status = query.state.data?.status === 200 ? query.state.data.data.status : null;
        return status === 'ready' || status === 'failed' ? false : exportPoll.intervalMs;
      },
    },
  });
  const current = job.data?.status === 200 ? job.data.data : null;
  const status = jobId ? (current?.status ?? 'queued') : 'idle';
  const failed = status === 'failed' || (!!jobId && job.isError);

  const begin = () => {
    setMessage(null);
    setJobId(null);
    start.mutate();
  };

  const download = async () => {
    if (!current?.downloadUrl) return;
    setMessage(null); // a refused try is not shown again once the next one starts
    try {
      await getSharer().deliver(current.downloadUrl);
    } catch (e) {
      setMessage(friendlyError(e));
    }
  };

  return (
    <Screen title="Export my data" subtitle="Download everything in your library as files you can keep.">
      {message ? (
        <Text tone="danger" accessibilityRole="alert">
          {message}
        </Text>
      ) : null}
      <Card>
        <Text>One zip with your items as a spreadsheet (CSV) and as JSON. It doesn&apos;t include photos.</Text>
        {failed ? (
          <>
            <Text tone="danger" accessibilityRole="alert">
              The export didn&apos;t finish. Start it again. If it still fails, try again in a few minutes.
            </Text>
            <Button label="Start the export again" busy={start.isPending} onPress={begin} />
          </>
        ) : status === 'ready' ? (
          <>
            <Text accessibilityRole="alert">Your export is ready.</Text>
            <Button label={isWeb() ? 'Download export' : 'Share export'} onPress={() => void download()} />
          </>
        ) : status === 'idle' ? (
          <Button label="Start export" busy={start.isPending} onPress={begin} />
        ) : (
          <LoadingState label={progress[status] ?? progress.queued} alert />
        )}
      </Card>
    </Screen>
  );
}
