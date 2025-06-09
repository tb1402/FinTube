# FinTube

Easily add content from YouTube to your Jellyfin installation

![](https://raw.githubusercontent.com/AECX/FinTube/master/Assets/logo.png)

## Why this fork was created

For some years I used a selfbuild PHP webpage as a simple frontend for yt-dlp.

As I migrated my media collection to Jellyfin in early 2025, I wanted it to have YT download support.

Thanks to AECX who created the original plugin this was pretty easy, but some features I'm used to from my webpage are missing.

Thus I've created this fork to integrate all the yt-dlp frontend features which are missing for me.

For this reason, if you create an issue, it may or may not be addressed, as the primary use case for this fork is currently my personal Jellyfin server.

Nevertheless I try my best to include all upstream commits, if they are compatible with my changes.

## Dependencies

FinTube requires [YouTube-DL/YouTube-DLP](https://github.com/yt-dlp/yt-dlp) and [id3v2](https://sourceforge.net/projects/id3v2/) for full functionality, however YT-DLP is sufficient for operation.

Check the link above to install YT-DLP and install id3v2:

- Debian/Ubuntu `# sudo apt install id3v2`
- Arch `# sudo pacman -S id3v2`

For other OS please check your package manager.

This fork also requires the built-in jellyfin ffmpeg version.
It should come preinstalled for the most versions of Jellyfin, but you can follow the [official guide for manual installation](https://jellyfin.org/docs/general/installation/advanced/manual/).

## Install

### Add my Repository

1. In your Admin Dashboard navigate to "Plugins"
2. Switch to the "Repositories" tab
3. Click "+" and add the Repository `https://raw.githubusercontent.com/tb1402/FinTube/refs/heads/master/manifest.json`
   Name it "FinTube tb1402" or "tb1402" - Or whatever helps you remember

### Install and configure the plugin

1. Switch to the "Catalog" tab
2. Search for the "FinTube" plugin and click install
3. Restart the Server and head back to the "Plugins" Sections
4. Click on FinTube and Select "Settings", enter a valid executable for yt-dlp/youtube-dl
5. Optionally: Enter a valid executable for id3v2 to be able to Tag Music with Artist, Title, Album and Track information

Now you are ready to go, head to the "FinTube" plugin page (at the bottom of your dashboard navigation), enter information as desired to start importing from YouTube.
