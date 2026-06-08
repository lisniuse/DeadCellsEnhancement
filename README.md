# SpeedTalisman

Dead Cells Core Modding mod that adds a Speed Talisman.

When equipped, the talisman increases weapon attack speed by modifying weapon strike timing data at runtime.

## Development debug config

For local testing only, create `debug_speed_config.json` next to the installed mod DLL:

```json
{
  "SpeedLevel": 2
}
```

`SpeedLevel` ranges from `1` to `5`. If the file is absent, the mod uses production scaling based on Boss Cell count, and 0 Boss Cells get no speed bonus.
