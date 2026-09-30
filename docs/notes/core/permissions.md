# Permissions

- **Permissions** (`Core/Permissions`, issue #32): what the menus may offer. Offline everything;
  online, spawning a vehicle from the travel menu is an admin's (it is left in the shared world),
  while equipment, getting into a vehicle already there, the place-search teleport (`/city` is a
  non-admin command anyway) and the fly camera stay free. The admin flag comes from the server
  (`ChatManager.AdminStatus`); the server re-checks for itself (`VehicleManager.MayPark`, the
  vehicles `admin-only-spawning` note).
