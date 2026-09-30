# The client needs its own request budget, not just the server's

- **The client needs its own request budget, not just the server's.** The LOD rings reach nine
  tiles out, so arriving somewhere new makes 361 tiles want their .terr at once — ~177 MB.
  Unbudgeted, the client floods the server, most requests are refused, and the retries fight:
  measured 1,135 refusals in 30 s while only 33 MB arrived. A six-slot semaphore in
  `NetworkChunkSource` took the same window to 226 MB at the full bandwidth cap.
