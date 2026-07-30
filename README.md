# Highlighter

**Team:** Highlighter — Anthony Cui (@AnthonyCui7)
**Category:** AI Agents (primary) · Azure OpenAI Models (secondary)

## What it does

Highlighter turns a raw VOD or livestream into finished video: paste a URL and
an agent pipeline captures, transcribes, and edits it into scored 9:16
highlight clips and/or a long-form cut. A studio web app lets you review the
results, chat with an editing agent (revise the cut, generate thumbnails,
research, reformat), and fine-tune everything in a full timeline editor —
cuts, speed, captions, text, reframing, music — then export and publish.

## Architecture

```
apps/web (Blazor Server studio + agent chat)
   → apps/api (ASP.NET Core Minimal API: auth, projects, jobs, editor renders)
      → pipeline-dotnet (worker: capture → transcribe → score → edit → render → publish)
         → pipeline/ (Python TransNetV2 sidecar for scene-cut detection)
   ⇄ Supabase (Postgres + Auth + Storage)
```

LLM work (clip scoring, long-form editing, revision/studio agents, thumbnails,
research) runs on Azure OpenAI with model fallbacks; agents are built on the
Microsoft Agent Framework.

## Tech stack

.NET 10 (ASP.NET Core Minimal API, Blazor Server), Microsoft Agent Framework,
Azure OpenAI, Azure AI Speech (transcription), Supabase (Postgres,
GoTrue auth, Storage), ffmpeg + yt-dlp + streamlink, PyTorch TransNetV2, Tailwind CSS v4.

## Getting started

Prereqs: .NET 10 SDK, ffmpeg/ffprobe, yt-dlp, a Supabase project, Python 3.11+
with `transnetv2-pytorch` (optional — scene cuts are skipped without it).

```bash
cp .env.example .env                 # fill in your own keys — never committed
dotnet build pipeline-dotnet         # the worker binary the API spawns
cd apps/api && dotnet run --project src/Highlighter.Api   # http://localhost:5199
cd apps/web && dotnet run                                  # http://localhost:5097
```

Apply `supabase/migrations/` to your Supabase project, open the web app,
create an account, and paste a video URL.

## Demo

`./demo/demo.mov`

## Known limits

Not fully deployed — backend and frontend currently run locally.
