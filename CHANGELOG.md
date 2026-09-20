### What's New in v1.1.0

- **Critical Security Fix**:
  - Enforced strict `Origin` header validation on the local WebSocket server to prevent Cross-Site WebSocket Hijacking (CSWSH).
  - Access is now strictly restricted to authorized origins; unauthorized origins are rejected with `403 Forbidden`.

- **Native Adapter Status Dialog**:
  - Added support for opening the native Windows "Network Connection Status" dialog directly by clicking the adapter name on the web dashboard.