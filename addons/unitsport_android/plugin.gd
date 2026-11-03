@tool
extends EditorPlugin

# Registers the Android export additions (gpx_open_export.gd). Editor only: nothing runs in the game.

var _export: EditorExportPlugin


func _enter_tree() -> void:
	_export = preload("gpx_open_export.gd").new()
	add_export_plugin(_export)


func _exit_tree() -> void:
	remove_export_plugin(_export)
	_export = null
