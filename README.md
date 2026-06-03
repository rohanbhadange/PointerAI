# ClickyClone

Windows desktop clone of Clicky. It runs as a tray-only app, uses `Ctrl+Alt` push-to-talk, sends screen context to a Cloudflare Worker, speaks responses with ElevenLabs, and shows a blue cursor overlay that can point at UI elements.

## Requirements

- .NET SDK 8.0 or newer for building
- .NET Desktop Runtime 8.0 or newer for running the framework-dependent installer build
- Windows 10/11
- A deployed Cloudflare Worker at `https://clickyclone.rohanbhadange18.workers.dev`
- Worker secrets configured for OpenAI, AssemblyAI, and ElevenLabs

## Build

```powershell
.\scripts\build.ps1
```

## Production Diagnostics

Runs live checks against the deployed Worker, including `/health`, `/transcribe-token`, `/tts`, and `/chat` with a real screenshot.

```powershell
.\scripts\run-worker-diagnostics.ps1
```

## Local Simulations

Runs deterministic local checks for coordinate mapping and multi-monitor edge cases.

```powershell
.\scripts\run-local-simulations.ps1
```

## Installer

Install Inno Setup, then compile:

```powershell
.\scripts\build-installer.ps1
```

The installer output is written to `artifacts\installer`.
