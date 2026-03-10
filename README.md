# Distributed Job Processing System

## Part 1 (current): Redis-backed queue + Worker

### Run everything

```bash
docker compose up --build
```

### Enqueue demo jobs

```bash
curl -X POST http://localhost:8080/jobs/send-email ^
  -H "Content-Type: application/json" ^
  -d "{\"to\":\"user@email.com\",\"subject\":\"Welcome\",\"body\":\"Hello\"}"
```

```bash
curl -X POST http://localhost:8080/jobs/image-processing ^
  -H "Content-Type: application/json" ^
  -d "{\"imageUrl\":\"https://example.com/image.jpg\",\"targetWidth\":256,\"targetHeight\":256}"
```

### Check status

Replace `{jobId}` with the id returned from enqueue.

```bash
curl http://localhost:8080/jobs/{jobId}
```
