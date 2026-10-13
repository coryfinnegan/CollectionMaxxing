# Deploying CollectionMaxxing (outline)

OPS-1 fills this in. See `docs/10-hosting-and-devops.md`.

1. Install or update Docker Desktop (WSL2) and cap WSL2 memory in `.wslconfig`.
2. Copy `.env.example` to `.env` and set the values (never commit `.env`).
3. The images are public on GHCR; no login is needed to pull.
4. Run the `migrate` one-shot, then start the stack (the deployer does this).
5. Expose the API with Tailscale Serve: `https://pc.tailnet.ts.net` to `127.0.0.1:5480`.
6. Check `/health/ready`, the Aspire dashboard (127.0.0.1 only) and the nightly backup.

Ports: the API uses 5480 (8080, 8409, 32400 and 8096 are taken by other apps). Postgres is not published.
