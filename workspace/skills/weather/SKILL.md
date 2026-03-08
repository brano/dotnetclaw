---
name: weather
description: Get current weather and forecasts (no API key required).
homepage: https://wttr.in/:help
metadata: {"dotnetclaw":{"emoji":"🌤️","requires":{"bins":["powershell"]}}}
---

# Weather

Two free services, no API keys needed.

## wttr.in (primary)

Quick one-liner:
```powershell
Invoke-RestMethod -s "wttr.in/Bratislava?format=3"
# Output: Bratislava: ⛅️ +10°C
```

Compact format:
```powershell
Invoke-RestMethod -s "wttr.in/Bratislava?format=%l:+%c+%t+%h+%w"
# Output: Bratislava: ⛅️ +15°C 71% ↙5km/h
```

Full forecast:
```powershell
Invoke-RestMethod -s "wttr.in/Bratislava?T"
```

Format codes: `%c` condition · `%t` temp · `%h` humidity · `%w` wind · `%l` location · `%m` moon

Tips:
- URL-encode spaces: `wttr.in/New+York`
- Airport codes: `wttr.in/BTS`
- Units: `?m` (metric) `?u` (USCS)
- Today only: `?1` · Current only: `?0`
- PNG: `curl -s "wttr.in/Bratislava.png" -o /tmp/weather.png`

## Open-Meteo (fallback, JSON)

Free, no key, good for programmatic use:
```powershell
Invoke-RestMethod -s "https://api.open-meteo.com/v1/forecast?latitude=51.5&longitude=-0.12&current_weather=true"
```

Find coordinates for a city, then query. Returns JSON with temp, windspeed, weathercode.

Docs: https://open-meteo.com/en/docs