# `kunstbaute` is a compound field

- **`kunstbaute` is a compound field.** `Bruecke mit Treppe`, `Gedeckte Bruecke`,
  `Bruecke mit Galerie`, `Unterfuehrung mit Treppe`, `Steg` — matching it with `switch`
  equality silently drops ~2,000 structures per country, and a bridge that loses its
  `Bridge` flag is draped, so it dives into the gorge it was crossing. Match with `Contains`.
