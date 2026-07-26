# README

For installation and configuration guides, please reference the Lidarr Wiki: https://wiki.servarr.com/en/lidarr/plugins#slskd

## Requirements

slskd 0.26.0 or newer is recommended. From that version the plugin enqueues downloads as batches and pins
their destination, so completed downloads are found regardless of the `transfers.download.destination.subdirectory`
expression configured in slskd. Older versions still work, but the destination is inferred from the remote
folder name and imports will fail if that expression is customised.

Set `Media Management > Allow Fingerprinting` to `Never`. Files shared on Soulseek often carry MusicBrainz
tags whose recording IDs disagree with Lidarr's metadata, and fingerprinting adds a second penalty for the
same mismatch, which is usually enough to push a release below the track match threshold and fail the
automatic import. The plugin raises a health check warning while fingerprinting is enabled.
