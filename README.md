Основная задача сервиса - дождаться завершения записи видео по событию на диск и отправить его в телеграм, в том числе разбив его на части, в случае необходимости. Исходники <a href="https://github.com/rsvln/frte2tg">тут</a><br><br>

# frte2tg

Frigate NVR → Telegram bridge. Subscribes to Frigate MQTT events and reviews, sends snapshots, video clips and animated previews to Telegram. Optionally analyzes snapshots with a local AI model via Ollama and performs face recognition via CompreFace.

## Features

- Handles both Frigate **events** and **reviews** (configurable per camera)
- Sends **snapshots** as media groups
- Concatenates and sends **video clips** via ffmpeg
- Splits large clips automatically
- Generates and sends **animated GIF previews** (optional, per camera)
- Re-publishes event/review to MQTT with type `trueend` when recordings are fully ready (optional, per camera)
- **Face recognition** via CompreFace — identifies known people in snapshots (optional, per camera)
- **AI-powered snapshot descriptions** via Ollama (optional, per camera) — if both FR and AI are enabled, recognized names are passed to Ollama as context
- Telegram rate limit handling with automatic retry
- Per-camera configuration: objects, zones, severity, triggers, behavior
- Web UI for log viewing and config editing (port 8888)
- Runs as a Docker container

## Requirements

- [Frigate NVR](https://frigate.video) with MQTT enabled
- MQTT broker
- Telegram bot token + local Bot API server (optional but recommended for large files)
- ffmpeg available in container
- Ollama instance with a vision model (optional, for AI descriptions)
- [CompreFace](https://github.com/exadel-inc/CompreFace) instance (optional, for face recognition)

## Quick Start

Image is available from both Docker Hub and GitHub Container Registry:

```bash
# Docker Hub
docker pull rsvln/frte2tg:latest

# GitHub Container Registry
docker pull ghcr.io/rsvln/frte2tg:latest
```

```yaml
# docker-compose.yml
services:
  frte2tg:
    image: ghcr.io/rsvln/frte2tg:latest  # or rsvln/frte2tg:latest
    restart: unless-stopped
    volumes:
      - /etc/frte2tg:/etc/frte2tg
      - /var/log/frte2tg:/var/log/frte2tg
      - /srv/frigate/clips:/srv/frigate/clips:ro
      - /srv/frigate/recordings:/srv/frigate/recordings:ro
      - /srv/frigate/config/frigate.db:/srv/frigate/config/frigate.db:ro
    ports:
      - "8888:8888"
```

## Configuration

Config file: `/etc/frte2tg/frte2tg.yml`

```yaml
frigate:
  host: 192.168.1.10
  port: 5000
  clipspath: /srv/frigate/clips
  dbpath: /srv/frigate/config/frigate.db
  recordingspath: /srv/frigate/recordings
  recordingsoriginalpath: /media/frigate/recordings
  cameras:
    - camera: frontdoor
      snapshot: true
      clip: true
      gif: false               # send animated GIF preview
      ai: false                # enable AI snapshot analysis for this camera
      fr: false                # enable face recognition for this camera
      trueend: false
      sctogether: false        # send snapshot and clip separately
      snapshottrigger: new     # new | update | end
      topic: reviews           # reviews | events
      severity:
        - alert
        - detection
      objects:
        - label: person
          percent: 50
        - label: dog
          percent: 70
      zones: []                # leave empty to ignore zones

mqtt:
  host: 192.168.1.10
  port: 1883
  user: mqtt
  password: mqtt
  eventstopic: frigate/events
  reviewstopic: frigate/reviews

telegram:
  token: YOUR_BOT_TOKEN
  chatids:
    - '-1001234567890'
  clipsizecheck: 2147483648    # 2GB — if clip exceeds this, split
  clipsizesplit: 2000000000    # split chunk size
  mediagrouplimit: 10
  sendchatstimepause: 30       # seconds between chats when multiple
  retryonratelimit: 30         # seconds to wait on Telegram 429 rate limit (fallback if Retry-After not provided)
  apiserver: http://192.168.1.10:8081/  # local bot API server, leave empty for cloud

options:
  timeoffset: 0                # minutes to add to UTC for display
  timeout: 500                 # seconds to wait for recordings to be ready
  retry: 30                    # polling interval in seconds
  sendeverythingwhatyouhave: true  # send partial clips if timeout expires
  gifwidth: 640                # GIF preview width in pixels (height is proportional)

logger:
  file: true
  console: true

# Optional: AI snapshot analysis via Ollama
ai:
  url: http://192.168.1.20:11434
  model: "qwen2.5vl:7b"
  humanprompt: "Кратко опиши что делает человек. Что он держит или несёт? 2-3 предложения. Не начинай с 'На изображении'. Не упоминай камеру, время, дату и текстовые метки."
  nonhumanprompt: "Кратко опиши что происходит. 2-3 предложения. Не начинай с 'На изображении'. Не упоминай камеру, время, дату и текстовые метки."
  numpredict: 150              # max tokens in Ollama response, limits description length
  temperature: 0.1             # lower = more deterministic, higher = more creative
  resizetowidth: 640           # resize image before sending to Ollama, 0 to disable

# Optional: face recognition via CompreFace
fr:
  url: http://192.168.1.20:8000
  apikey: YOUR_COMPREFACE_API_KEY
  confidence: 0.8              # minimum similarity to consider a match (0.0 - 1.0)
  detprobthreshold: 0.8        # minimum probability that detected area is actually a face (0.0 - 1.0)
```

### Camera options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `snapshot` | bool | `true` | Send snapshots |
| `clip` | bool | `false` | Send video clips |
| `gif` | bool | `false` | Send animated GIF preview generated from the clip |
| `ai` | bool | `false` | Enable AI snapshot analysis for this camera (requires `ai` section) |
| `fr` | bool | `false` | Enable face recognition for this camera (requires `fr` section) |
| `trueend` | bool | `false` | Re-publish event/review to the same MQTT topic with `type: trueend` once all recordings are confirmed ready in Frigate DB. Useful for triggering downstream automations only when the clip is complete. |
| `sctogether` | bool | `false` | Send snapshot and clip in one media group |
| `snapshottrigger` | string | `end` | When to send snapshot: `new`, `update`, or `end` |
| `topic` | string | `reviews` | Which MQTT topic to use: `reviews` or `events` |
| `severity` | list | `[detection, alert]` | Frigate review severity filter |
| `objects` | list | `[]` | Filter by object label and minimum confidence percent |
| `zones` | list | `[]` | Filter by Frigate zone names (empty = all zones) |

## GIF Previews

When `gif: true` is set for a camera, frte2tg generates an animated GIF from the recorded clip using ffmpeg (8 fps, 8x speed) and sends it as a separate Telegram animation. Width is controlled globally via `options.gifwidth`.

## Face Recognition

When the `fr` section is present and `url`/`apikey` are set, enabling `fr: true` on a camera will run face recognition on snapshots via [CompreFace](https://github.com/exadel-inc/CompreFace) before posting results to Telegram.

If `ai: true` is also enabled on the camera, recognized names are automatically passed to Ollama as context, enriching the description prompt. If only `fr` is enabled without `ai`, the caption is updated with recognized names directly.

To set up CompreFace, use the provided `docker-compose-compreface.yml`, then open the web UI, create an application, add a Recognition Service, and upload face photos for each person via the Train section.

## AI Analysis

When the `ai` section is present and `url`/`model` are set, enabling `ai: true` on a camera will:

1. Send all snapshots to Ollama after posting to Telegram
2. Edit the Telegram message caption with AI descriptions for each snapshot

Uses the `humanprompt` if Frigate detected a person, `nonhumanprompt` otherwise. If face recognition is also enabled, recognized names are prepended to the prompt automatically.

Tested with `qwen2.5vl:7b` on a machine with RTX 3060 — ~2 seconds per image.

## Web UI

Available at `http://<host>:8888`

- Live log viewer with filtering by type, camera, text
- Color-coded by event ID
- Config editor with backup on save

## Building

Pushing to `master` branch triggers an automatic build via GitHub Actions and publishes the image to `ghcr.io/rsvln/frte2tg:latest`.

To build manually:

```bash
docker build -t rsvln/frte2tg:latest -f frte2tg/Dockerfile .
docker push rsvln/frte2tg:latest
```

## License

MIT