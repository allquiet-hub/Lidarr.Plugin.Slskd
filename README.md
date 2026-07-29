# Slskd Plugin for Lidarr

Adds [Soulseek](https://www.slsknet.org/) to Lidarr through [slskd](https://github.com/slskd/slskd).

The plugin registers **both halves** of the pipeline: an indexer that searches the Soulseek network
and a download client that enqueues the transfers. You need to add both in Lidarr — one without the
other does nothing.

## Requirements

| | Version | Why |
|---|---|---|
| Lidarr | `nightly` | Plugin support is not in the stable channel. Use `lscr.io/linuxserver/lidarr:nightly`. |
| slskd | 0.26.0 or newer | Older versions work, but see [slskd version](#slskd-version) below. |

A working slskd instance with a Soulseek account already configured, and an slskd **API key** with
the `readwrite` role.

## Installation

1. In Lidarr, go to **System > Plugins**.
2. Paste `https://github.com/allquiet-hub/Lidarr.Plugin.Slskd` into the GitHub URL field and press
   **Install**.
3. Restart Lidarr when prompted.

## Configuration

### 1. Download client

**Settings > Download Clients > + > Slskd**

| Field | Value |
|---|---|
| Host / Port | Where slskd listens, e.g. `192.168.1.10` and `5030` |
| Use SSL | Only if slskd serves HTTPS |
| Url Base | Only if slskd sits behind a reverse proxy on a sub-path |
| API Key | An slskd API key with the `readwrite` role |

**Test** reports a warning rather than an error if slskd is older than 0.26.0 — the plugin still
works, but read [slskd version](#slskd-version).

### 2. Indexer

**Settings > Indexers > + > Slskd**

| Field | Default | What it does |
|---|---|---|
| URL | `http://localhost:5030/` | Same instance as the download client |
| API Key | — | Same key as the download client |
| Ignored Users | empty | Extra users to skip. Your own account is excluded automatically, so this is only for other peers. The key is just a label. |
| Early Download Limit | empty | Days before the release date that downloads are allowed |
| Minimum Upload Speed | `0` | Hides peers slower than this, in MB/s. Decimals allowed (`0.2`). `0` shows everyone. |
| Maximum Queue Length | `0` | Hides peers with more uploads already queued. `0` shows everyone. |
| Response Limit | `50` | Ends a search once this many users have responded. `0` waits for the whole search. |
| Allow Incomplete Releases | off | Offers folders with fewer audio files than the album has tracks |

The peer filters default to off on purpose, and the reason is structural rather than a matter of
taste. They drop responses inside the indexer, so a filtered peer never enters Lidarr's release list.
A release that *is* listed can always be forced, however badly it scores — grabbing one by hand posts
it straight to the download client without consulting the decision specifications — whereas one that
was filtered out has nothing left to force. Raise these only if you are drowning in unusable results.

## How it works

A search asks slskd for the artist and album, then groups the responses by remote folder — one
folder becomes one release. Grabbing a release enqueues an slskd **batch** whose destination is
pinned to `lidarr/<download id>/<Artist> - <Album>/`, which is how the plugin recognises the
download later and how Lidarr identifies what it imported. Once Lidarr has imported the album and
removed it from the queue, the plugin deletes that folder from slskd.

## slskd version

From 0.26.0 the plugin pins each download's destination through the batch API, so completed files
land where the plugin expects **regardless** of the `transfers.download.destination.subdirectory`
expression in your slskd config. On older versions the destination has to be inferred from the
remote folder name instead, and imports break if that expression has been customised. 0.26.0 also
enables automatic retries of failed transfers.

Two slskd settings are worth checking, since neither is obvious:

- `transfers.download.destination.permissions.mode` is applied to **directories** as well as files,
  per slskd's own documentation. A mode without the execute bit, such as `666`, leaves album folders
  that cannot be traversed, so prefer `777`. If imports work today on a mode like that, they are
  relying on Lidarr running as a user that bypasses the check, and will break the day it doesn't.
- `transfers.download.slots` is generous by default — slskd's example config ships `500`, and it is a
  hard cap on transfers running at once. Lidarr imports an album only once every file in it has
  completed, so the fewer downloads run in parallel, the sooner any one of them finishes. Whether
  that is worth tuning depends on your line; slot changes need an slskd restart, speed limits do not.

## Known issues

- **Releases that show as unparseable.** How a folder is named is entirely up to whoever shares it.
  Plenty of people tag their library with Picard, and those folders read straight off as artist and
  album; plenty of others don't, and you get share roots, quality buckets, bootleg packs and
  free-form names that no parser can turn into a release. The plugin rewrites titles so the match
  holds where the folder name alone would not carry it, but some releases still reach Lidarr without
  a recognised artist. They stay grabbable from interactive search — that is deliberate, since a
  visible result you can force beats a result that was filtered away.
- **Albums whose MusicBrainz duration is 0 are rejected automatically**, with `Album duration is 0`.
  Lidarr bounds an acceptable size by the runtime, so with no runtime it has nothing to check against
  and refuses permanently. The runtime is summed over the album's *monitored* releases, or over all of
  them while "Any Release OK" is set, so when another release of the same album does carry track
  lengths, pointing the album at it clears the rejection; when the only release has none, nothing in
  Lidarr's settings will. Nothing about this is specific to the plugin, and it does not actually block
  you — grabbing the release by hand from interactive search posts it to the download client without
  consulting the specifications at all. The durable fix is adding the track lengths on MusicBrainz.
- **`Worst track match: … [recording id]`** rejects an album whose every other detail is right. The
  recording ID is weighted 10 in Lidarr's per-track score against at most 8 for the title, artist,
  length and track number combined, and an album is judged on its *worst* track against a limit of
  0.40 — so a file whose MusicBrainz tag names a different recording scores 10/18 and fails on that
  key alone, no matter how perfect the rest of the match is. A file with no recording ID tag is not
  scored on the key at all, which makes stripping the MusicBrainz tags a fix. So is a manual import:
  for files not arriving from a download client, Lidarr checks the album distance and skips the
  worst-track test entirely. Leaving fingerprinting on is worth a try as well — being rejected on
  this key is itself what triggers it, and a corroborating AcoustID result adds a second, passing
  entry under the same key, which raises the divisor without raising the total and takes the track to
  10/28, back under the limit. Whether AcoustID corroborates any given file is the part nobody can
  promise.
- **`Album match is not close enough … [album, year, label, album id, unmatched tracks]`** means the
  folder describes a different release from the one being imported into. Files the chosen release has
  no room for count as unmatched and are scored as a penalty of their own, so a folder holding more
  than the album asked for is rejected even when every track it does contain maps at zero distance —
  a pack containing two albums, grabbed for one of them, is the clearest way to hit this. Pick a
  release whose folder covers exactly one album.
- **Fingerprinting can silently do nothing on a large release.** Lidarr fingerprints every track of a
  release into a single request — no chunking — which for tracks over two minutes costs about 2.5 KB
  of compressed body each. A big enough folder is refused with `413 Request Entity Too Large`; a
  30-track double album was. Unlike a rate-limit reply that one is never retried, and it is logged
  only at debug level, so the setting reads as active while nothing is looked up. Where the limit
  actually falls is untested.
- **Empty folders pile up in slskd's `incomplete` directory.** Its `retention.files.incomplete`
  setting is defined over files, deleting them once their last access time passes the configured age;
  the documentation says nothing about directories. Lowering it clears the stale files sooner but
  leaves the folders, so removing those is a job for the host.

For general Lidarr plugin documentation, see the
[Lidarr Wiki](https://wiki.servarr.com/en/lidarr/plugins#allquiet-hublidarrpluginslskd).
