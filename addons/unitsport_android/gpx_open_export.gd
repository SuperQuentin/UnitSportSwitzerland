@tool
extends EditorExportPlugin

# Lets other apps open or share a .gpx with UnitSport (#1029): an exported activity-alias onto
# GodotApp with VIEW and SEND intent filters. Not inside GodotApp itself: Godot exports that
# activity as android:exported="false", where no other app may reach it. The game reads the intent
# (AndroidBridge.OpenedFile) and starts the replay. Needs the Gradle build: the prebuilt template
# takes no manifest additions. Note: docs/notes/gpx/open-with.md.

const OPENER := """
		<activity-alias
			android:name=".GpxOpener"
			android:targetActivity=".GodotApp"
			android:label="@string/godot_project_name_string"
			android:exported="true">
			<!-- a file manager or Drive opening a .gpx: by MIME type when the provider knows it -->
			<intent-filter>
				<action android:name="android.intent.action.VIEW" />
				<category android:name="android.intent.category.DEFAULT" />
				<category android:name="android.intent.category.BROWSABLE" />
				<data android:scheme="content" />
				<data android:scheme="file" />
				<data android:mimeType="application/gpx+xml" />
				<data android:mimeType="application/gpx" />
			</intent-filter>
			<!-- ... and by extension when it calls it octet-stream (the path pattern only matches file and
			     content URIs that end in .gpx, which many providers' do not) -->
			<intent-filter>
				<action android:name="android.intent.action.VIEW" />
				<category android:name="android.intent.category.DEFAULT" />
				<category android:name="android.intent.category.BROWSABLE" />
				<data android:scheme="content" />
				<data android:scheme="file" />
				<data android:host="*" />
				<data android:mimeType="*/*" />
				<data android:pathPattern=".*\\\\.gpx" />
				<data android:pathPattern=".*\\\\..*\\\\.gpx" />
				<data android:pathPattern=".*\\\\.GPX" />
			</intent-filter>
			<!-- Share to UnitSport (Strava, mail, Drive) -->
			<intent-filter>
				<action android:name="android.intent.action.SEND" />
				<category android:name="android.intent.category.DEFAULT" />
				<data android:mimeType="application/gpx+xml" />
				<data android:mimeType="application/gpx" />
				<data android:mimeType="application/octet-stream" />
				<data android:mimeType="text/xml" />
				<data android:mimeType="application/xml" />
			</intent-filter>
		</activity-alias>
"""


func _get_name() -> String:
	return "UnitSportGpxOpen"


func _supports_platform(platform: EditorExportPlatform) -> bool:
	return platform is EditorExportPlatformAndroid


func _get_android_manifest_application_element_contents(_platform: EditorExportPlatform, _debug: bool) -> String:
	return OPENER
