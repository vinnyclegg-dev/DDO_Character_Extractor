# Character Extractor — Dungeon Helper plugin starter

Reads your current character via the VoK SDK and writes a JSON snapshot to
`%APPDATA%\DungeonHelper\extracts\` on login and on level-up.

## Build

1. Install the .NET 8 SDK.
2. The csproj expects Dungeon Helper at `%AppData%\Dungeon Helper\`
   (i.e. `C:\Users\<you>\AppData\Roaming\Dungeon Helper\`). If yours is
   somewhere else, override at build time:

   ```
   dotnet build -c Release -p:DungeonHelperDir="D:\path\to\Dungeon Helper\"
   ```

   (The trailing backslash is required.)

3. From this folder:

   ```
   dotnet build -c Release
   ```

## Install

Copy the two files from `bin\Release\net8.0-windows\` into the Dungeon Helper
plugins folder:

```
%AppData%\Dungeon Helper\plugins\VoK.CharacterExtractor.dll
%AppData%\Dungeon Helper\plugins\VoK.CharacterExtractor.metadata
```

Restart Dungeon Helper. The plugin should appear in its plugin list.

## Caveats

- The exact parameter list of `IDdoPlugin.Initialize` is determined by the
  SDK version your DH install ships. If the compiler complains about the
  signature in `CharacterExtractorPlugin.Initialize(IDdoGameDataProvider)`,
  open `VoK.Sdk.dll` in your IDE's object browser to see the real signature
  and add the extra parameters.
- DH ignores the `UpdateSource`/`AllowedVersions` fields in the metadata
  when they're empty, so it won't try to auto-update your local plugin.
- Read-only is fine. Writing back to the game (the `SendInput` /
  `SelectObject` / movement-setter paths in `IDdoGameDataProvider`) drifts
  toward automation — keep the plugin read-only and you stay within the
  same lane every other VoK plugin uses.
