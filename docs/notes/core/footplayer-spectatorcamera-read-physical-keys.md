# `FootPlayer` and `SpectatorCamera` read PHYSICAL keys every frame

- **`FootPlayer` and `SpectatorCamera` read PHYSICAL keys every frame**, so a focused
  `LineEdit` does not stop them: typing "west" in chat walks you into a lake. Every text-entry
  UI registers with `Core/UiFocus` and both controllers check it.
