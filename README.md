# 📺 Jellyfin YouTube Plugin (SmartTube 1:1 Parity Edition)

[![CI](https://github.com/FaserF/youtube-jellyfin/actions/workflows/ci.yml/badge.svg)](https://github.com/FaserF/youtube-jellyfin/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/FaserF/youtube-jellyfin?label=Latest%20Release)](https://github.com/FaserF/youtube-jellyfin/releases)
[![Jellyfin 10.10+](https://img.shields.io/badge/Jellyfin-10.10%2B%20%7C%2010.11%20(.NET%2010)-00A4DC?logo=jellyfin)](https://jellyfin.org)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)

**The complete 1:1 SmartTube Android TV replacement and YouTube Metadata/Streaming suite for Jellyfin.**

---

## ✨ Key Features

### 1. 📺 SmartTube 1:1 Native Jellyfin Channel (`YouTubeChannel`)
Browsable directly on **Android TV, Fire TV, Samsung Tizen, LG webOS, Apple TV (Swiftfin), Mobile, and Web** without leaving Jellyfin:
- **🏠 Home & Recommended** (`FEwhat_to_watch` via YouTube `TVHTML5` Innertube API)
- **📺 Subscriptions Feed** (`FEsubscriptions` chronological uploads from subscribed channels)
- **▶️ Continue Watching & History** (`FEhistory` with automatic `[▶ XX%]` resume badges)
- **👤 Subscribed Channels (A–Z)** (browse every subscribed channel and its latest videos)
- **⏱️ Watch Later (`VLWL`) & 👍 Liked Videos (`VLLV`)**
- **📁 My Playlists** (browse personal and saved playlists)
- **🔥 Trending, 🎮 Gaming, 🎵 Music & 🔴 Live Streams**

### 2. 👥 Per-User YouTube OAuth TV Login & Public Fallback
- **Primary Account Auto-Link:** Automatically links the primary Jellyfin user (`fabian`) to their personal YouTube account.
- **Multi-User TV Device Code Flow:** Every Jellyfin user can independently link their own personal YouTube account via `https://www.google.com/device` (`TVHTML5` OAuth device flow) in **Dashboard → Plugins → YouTube** or directly from the channel.
- **Unlinked User Isolation:** Unlinked Jellyfin users never see another user's private feeds; they automatically receive **Public/Anonymous YouTube** (Trending, Popular, Gaming, Music, Live) or can have the channel hidden entirely via `EnablePublicFallbackForUnlinkedUsers`.

### 3. 🛡️ Dual-Layer SponsorBlock Auto-Skip (Android TV + Web)
- **Server-Side Session Auto-Skip (`YouTubeStartupService`):** Monitors `ISessionManager.PlaybackProgress` across **all** Jellyfin clients (including native Android TV / Fire TV ExoPlayer) and issues instant `PlaystateCommand.Seek` commands when playback enters a SponsorBlock segment (`sponsor`, `selfpromo`, `interaction`, `intro`, `outro`).
- **Web / Desktop Client Script (`youtubePlugin.js`):** Real-time HTML5 `<video>` `timeupdate` listener with toast notifications (`⏭️ SponsorBlock skipped: sponsor`), ReturnYouTubeDislike badge injection, and top comments drawer.

### 4. 👍 ReturnYouTubeDislike & ♥️ Favorite-to-Like Sync
- **ReturnYouTubeDislike Integration:** Injects live community Likes, Dislikes, approval percentage (`👍 12.5K  👎 250 (98%)`), and 0–10 star rating into every video item.
- **Two-Way Rating Sync:** Marking any YouTube video as a **Favorite (♥️)** on Android TV, Mobile, or Web automatically triggers `videos/rate?rating=like` on YouTube for that Jellyfin user.

### 5. 🎬 4K Ad-Free Direct Streaming & Proxy Rewriting
- Resolves up to **4K (2160p) / 1080p** streams via `yt-dlp` / Innertube with optional SOCKS5 proxy routing (`socks5://easyproxy-sports:1080`).
- Automatically rewrites IP-bound `googlevideo.com` HLS `.m3u8` manifests and segments through `/api/youtubePlugin/proxy?u=...` so any client on LAN or Tailnet can direct-play without IP mismatch errors.

### 6. 📚 Full Metadata, Artwork & `.strm` Library Suite
- **Providers:** `YouTubeSeriesProvider` (Channels), `YouTubeSeasonProvider` (Playlists/Seasons), `YouTubeEpisodeProvider` (Videos), `YouTubeMovieProvider` (Standalone Movies/Documentaries), `YouTubeImageProvider` (`maxresdefault.jpg` / Channel Banners), and `YouTubeExternalId` / `YouTubeExternalUrlProvider`.
- **Scheduled Tasks:**
  - `SyncYouTubeStrmTask`: Automatically syncs subscribed channels into `/data/youtube/<Channel [YouTubeChannel-UC...]>/Season 01/S01E01 - <Title [YouTubeVideo-...]>.strm` for global Jellyfin search.
  - `SyncYouTubeWatchHistoryTask`: Syncs YouTube `FEhistory` watch progress and completed state into Jellyfin user profiles.
  - `ClearYouTubeIDsTask`: Resets external YouTube provider IDs across library items.

---

## 📦 Installation

### Option A: Jellyfin Plugin Repository (Recommended)
1. In Jellyfin, go to **Dashboard → Plugins → Repositories → Add**.
2. Repository Name: `FaserF YouTube Repository`
3. Repository URL:
   ```text
   https://raw.githubusercontent.com/FaserF/youtube-jellyfin/main/manifest.json
   ```
4. Install **YouTube** from the **Channels** catalog and restart Jellyfin.

### Option B: Manual `.dll` Installation
1. Download `youtube.zip` from the [Latest Release](https://github.com/FaserF/youtube-jellyfin/releases).
2. Extract into `/config/data/plugins/YouTube/` and restart Jellyfin.

---

## 🧪 Building & Testing

```bash
dotnet restore
dotnet build -c Release
dotnet run --project Jellyfin.Plugin.YouTube.Tests -c Release --no-build
```
