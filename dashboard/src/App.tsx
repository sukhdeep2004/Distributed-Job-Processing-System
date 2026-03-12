import { useEffect, useState } from 'react';
import './App.css';

type Job = {
  id: string;
  type: string;
  status: string;
  createdAt: string;
  startedAt?: string | null;
  finishedAt?: string | null;
  retryCount: number;
  result?: string | null;
  errorMessage?: string | null;
};

type QueueStats = {
  queueHigh: number;
  queueDefault: number;
  queueLow: number;
  deadLetter: number;
  activeWorkers: number;
};

const API_BASE =
  import.meta.env.VITE_API_BASE ?? 'http://localhost:8080';

function App() {
  const [jobs, setJobs] = useState<Job[]>([]);
  const [stats, setStats] = useState<QueueStats | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [retryingId, setRetryingId] = useState<string | null>(null);

  const loadJobs = async () => {
    try {
      setLoading(true);
      setError(null);
      const [jobsRes, statsRes] = await Promise.all([
        fetch(`${API_BASE}/jobs?page=1&pageSize=50`),
        fetch(`${API_BASE}/jobs/queue-stats`),
      ]);
      if (!jobsRes.ok) throw new Error(`Jobs: ${jobsRes.status}`);
      if (!statsRes.ok) throw new Error(`Stats: ${statsRes.status}`);
      const jobsData = (await jobsRes.json()) as Job[];
      const statsData = (await statsRes.json()) as QueueStats;
      setJobs(jobsData);
      setStats(statsData);
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setLoading(false);
    }
  };

  const retryJob = async (jobId: string) => {
    try {
      setRetryingId(jobId);
      const res = await fetch(`${API_BASE}/jobs/${jobId}/retry`, { method: 'POST' });
      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        throw new Error((body as { error?: string }).error ?? `Retry failed (${res.status})`);
      }
      await loadJobs();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setRetryingId(null);
    }
  };

  useEffect(() => {
    loadJobs();
    const id = setInterval(loadJobs, 5000);
    return () => clearInterval(id);
  }, []);

  return (
    <div className="app">
      <header className="app-header">
        <h1>Distributed Job Dashboard</h1>
        <button onClick={loadJobs} disabled={loading}>
          {loading ? 'Refreshing…' : 'Refresh'}
        </button>
      </header>

      {error && <div className="alert error">{error}</div>}

      {stats != null && (
        <section className="queue-stats">
          <span>High: {stats.queueHigh}</span>
          <span>Default: {stats.queueDefault}</span>
          <span>Low: {stats.queueLow}</span>
          <span>Dead letter: {stats.deadLetter}</span>
          <span>Workers: {stats.activeWorkers}</span>
        </section>
      )}

      <main>
        <table className="jobs-table">
          <thead>
            <tr>
              <th>Job ID</th>
              <th>Type</th>
              <th>Status</th>
              <th>Retries</th>
              <th>Created</th>
              <th>Started</th>
              <th>Finished</th>
              <th>Result / Error</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {jobs.length === 0 && !loading && (
              <tr>
                <td colSpan={9} className="empty">
                  No jobs yet. Submit one via the API.
                </td>
              </tr>
            )}
            {jobs.map((job) => (
              <tr key={job.id}>
                <td className="mono">{job.id}</td>
                <td>{job.type}</td>
                <td className={`status status-${job.status.toLowerCase()}`}>
                  {job.status}
                </td>
                <td>{job.retryCount}</td>
                <td>{new Date(job.createdAt).toLocaleString()}</td>
                <td>
                  {job.startedAt
                    ? new Date(job.startedAt).toLocaleString()
                    : '—'}
                </td>
                <td>
                  {job.finishedAt
                    ? new Date(job.finishedAt).toLocaleString()
                    : '—'}
                </td>
                <td className="result">
                  {job.errorMessage
                    ? job.errorMessage
                    : job.result ?? '—'}
                </td>
                <td>
                  {job.status === 'FAILED' && (
                    <button
                      type="button"
                      className="retry-btn"
                      onClick={() => retryJob(job.id)}
                      disabled={retryingId === job.id}
                    >
                      {retryingId === job.id ? 'Retrying…' : 'Retry'}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </main>
    </div>
  );
}

export default App;
