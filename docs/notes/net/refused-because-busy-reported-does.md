# "Refused because busy" must not be reported as "does not exist"

- **"Refused because busy" must not be reported as "does not exist".** The first version sent
  one `AssetMissing` for both, and `NetworkChunkSource` cached it, so a momentary backlog
  blanked those tiles for the whole session. There is now a separate `AssetBusy`, and only a
  permanent miss is remembered.
