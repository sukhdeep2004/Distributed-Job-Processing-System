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

const API_BASE =
  import.meta.env.VITE_API_BASE ?? 'http://localhost:8080';

function App() {
  const [jobs, setJobs] = useState<Job[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const loadJobs = async () => {
    try {
      setLoading(true);
      setError(null);
      const res = await fetch(`${API_BASE}/jobs?page=1&pageSize=50`);
      if (!res.ok) {
        throw new Error(`Failed to load jobs (${res.status})`);
      }
      const data = (await res.json()) as Job[];
      setJobs(data);
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setLoading(false);
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
            </tr>
          </thead>
          <tbody>
            {jobs.length === 0 && !loading && (
              <tr>
                <td colSpan={8} className="empty">
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
              </tr>
            ))}
          </tbody>
        </table>
      </main>
    </div>
  );
}

export default App;
