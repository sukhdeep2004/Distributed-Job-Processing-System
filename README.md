# Distributed Job Processing System

End‑to‑end distributed background job system using **.NET 8**, **Redis**, **PostgreSQL**, and a **React dashboard**.

## 1. Run the stack

From the project root:

```bash
docker compose up --build
```

This starts:

- API (`http://localhost:8080`)
- Worker
- Redis
- PostgreSQL
- Dashboard (`http://localhost:3000`)

## 2. Enqueue demo jobs

In Git Bash / Linux (using `\` for line continuation):

Add `?priority=high` or `?priority=low` to use priority queues (default is normal).

```bash
curl -X POST "http://localhost:8080/jobs/send-email?priority=high" \
  -H "Content-Type: application/json" \
  -d "{\"to\":\"user@email.com\",\"subject\":\"Welcome\",\"body\":\"Hello\"}"
```

```bash
curl -X POST http://localhost:8080/jobs/image-processing \
  -H "Content-Type: application/json" \
  -d "{\"imageUrl\":\"https://example.com/image.jpg\",\"targetWidth\":256,\"targetHeight\":256}"
```

Workers process **high** before **default** before **low**. The dashboard shows queue sizes and active worker count.

The API responds with:

```json
{"id":"job_xxx..."}
```

## 3. Check job status

### Via API

Replace `{jobId}` with the id from the enqueue response:

```bash
curl http://localhost:8080/jobs/{jobId}
```

To list recent jobs (paged):

```bash
curl "http://localhost:8080/jobs?page=1&pageSize=50"
```

Queue and worker stats:

```bash
curl http://localhost:8080/jobs/queue-stats
```

Retry a failed job (re-queues with stored payload):

```bash
curl -X POST http://localhost:8080/jobs/{jobId}/retry
```

### Via dashboard

Open:

- `http://localhost:3000`

The dashboard shows:

- Job ID, type, status
- Retry count
- Created / started / finished timestamps
- Result or error message
- **Queue stats**: high / default / low queue length, dead-letter count, active workers
- **Retry** button for failed jobs (re-queues the job)

It auto‑refreshes every few seconds and can also be refreshed manually with the **Refresh** button.

## 4. Logging + metrics (Part 5)

### Serilog structured logging

- API and Worker use **Serilog** and write structured logs to stdout (great for Docker).

### Prometheus metrics

- API exposes Prometheus metrics at:
  - `http://localhost:8080/metrics`
- Prometheus runs at:
  - `http://localhost:9090`

Example metrics to query in Prometheus:

- `jobs_enqueued_total`
- `jobs_retry_requested_total`
