# Nudge

Windows desktop companion. It runs as a tray-only app, uses `Ctrl+Alt` push-to-talk, sees the screen, speaks responses, and can point the cursor buddy at UI elements.

## Download

Download the latest Windows installer from:

```text
https://github.com/rohanbhadange/PointerAI/releases
```

The release installer is built as a self-contained Windows app, so users do not need to install the .NET Desktop Runtime separately. After installing, users only need their provider keys for the setup path they choose. Once a release includes `NudgeSetup.exe`, you can also link directly to `https://github.com/rohanbhadange/PointerAI/releases/latest/download/NudgeSetup.exe`.

## Choose One Setup Path

Nudge can run with local keys on your computer or through your own Cloudflare Worker.

### Option A: Local keys

This is the lowest-friction path and does not require Cloudflare.

1. Launch `Nudge.exe`.
2. Choose **Use local keys on this computer**.
3. Paste:
   - `OPENAI_API_KEY`
   - `ASSEMBLYAI_API_KEY`
   - `ELEVENLABS_API_KEY`
   - `ELEVENLABS_VOICE_ID`
4. The app creates a local `.env` next to `Nudge.exe`.

Real `.env` files are ignored by Git. `.env.example` is included as a template only.

### Option B: Cloudflare Worker

Use this if you want provider keys stored as Cloudflare Worker secrets instead of in a local `.env`.

1. Install Node.js.
2. From this repo, run:

```powershell
.\scripts\setup-worker.ps1
```

3. Copy the `workers.dev` URL printed by Wrangler.
4. Open the Worker in the Cloudflare dashboard.
5. Add these Worker secrets:
   - `OPENAI_API_KEY`
   - `ASSEMBLYAI_API_KEY`
   - `ELEVENLABS_API_KEY`
   - `ELEVENLABS_VOICE_ID`
6. Launch `Nudge.exe`.
7. Choose **Use a Cloudflare Worker URL** and paste the Worker URL.

The app validates `/health` and `/diagnostics` before saving the Worker URL.

To check the Worker script setup without deploying:

```powershell
.\scripts\setup-worker.ps1 -DryRun
```

## Requirements

For users:

- Windows 10/11
- Provider keys for OpenAI, AssemblyAI, and ElevenLabs
- Node.js only if using the Cloudflare Worker setup path

For developers:

- .NET SDK 8.0 or newer for building
- Node.js for Worker checks and Cloudflare Worker setup
- Inno Setup 6 for compiling the Windows installer

## Build

```powershell
.\scripts\build.ps1
```

## Local Simulations

Runs deterministic local checks for coordinate mapping, setup parsing, and multi-monitor edge cases.

```powershell
.\scripts\run-local-simulations.ps1
```

## Production Diagnostics

Runs live checks against the configured Worker, including `/health`, `/transcribe-token`, `/tts`, and `/chat` with a real screenshot.

```powershell
.\scripts\run-worker-diagnostics.ps1
```

## Installer

The installer includes the published self-contained Windows app. Install Inno Setup, then compile:

```powershell
.\scripts\build-installer.ps1
```

The installer output is written to `artifacts\installer\NudgeSetup.exe`.

## Updates

Installed apps check GitHub Releases for new versions in the background. Users can also right-click the tray icon and choose **Check for updates**.

- Local keys users keep their installed `.env` through app updates.
- Worker users keep their Worker URL through app updates.
- If a release changes Worker code, Worker users may need to rerun:

```powershell
.\scripts\setup-worker.ps1
```

To package release artifacts locally:

```powershell
.\scripts\build-release.ps1
```

Tag releases as `v1.0.1`, `v1.1.0`, and so on. The GitHub release workflow uploads `NudgeSetup.exe`, which the app uses for automatic updates.

## Landing Page

The static product page lives in `landing`. Open `landing\index.html` directly to preview it locally, or publish the folder with GitHub Pages or Cloudflare Pages. The primary landing page button points to the GitHub Releases page:

```text
https://github.com/rohanbhadange/PointerAI/releases
```
