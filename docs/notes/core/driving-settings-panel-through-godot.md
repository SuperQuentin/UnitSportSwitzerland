# Driving the Settings panel through the godot-ai MCP changes real settings

- **Driving the Settings panel through the godot-ai MCP changes real settings.** A mouse wheel
  over the scrolling panel lands on whichever slider is under the pointer and `Commit` saves it to
  `user://settings.json` at once (day length went 24 -> 23 that way). An `OptionButton`'s popup is
  its own window, so it does not show in a `game` capture, and arrow keys move focus between rows
  rather than through the list. Read the panel with `get_ui_elements` instead, and put anything a
  test touched back afterwards.
