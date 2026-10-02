# Never default the world origin to LV95 0/0

- **Never default the world origin to LV95 0/0.** Switzerland is 2.6 million metres from
  there, so float precision collapses the moment real data arrives. With no manifest the origin
  is the spawn point (`ClientWorld`), the server's is the default spawn (`ServerWorld`), and with no
  terrain at all `WorldOrigin.SwissDefault()` is the centre of Switzerland.
- Since #185 the starting origin only says where world space begins: the floating origin moves it
  with the camera (`floating-origin`), and a client joining a server keeps its own, since positions
  on the wire are LV95. There is no rebase or world reset on connect any more.
