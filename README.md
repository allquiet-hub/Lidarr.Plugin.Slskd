# Slskd Plugin for Lidarr

Adds [Soulseek](https://www.slsknet.org/) to Lidarr through [slskd](https://github.com/slskd/slskd).

The plugin registers **both halves** of the pipeline: an indexer that searches the Soulseek network
and a download client that enqueues the transfers. You need to add both in Lidarr — one without the
other does nothing.

## Requirements

| | Version | Why |
|---|---|---|
| Lidarr | `nightly` | Plugin support is not in the stable channel. Use `lscr.io/linuxserver/lidarr:nightly`. |
| slskd | 0.26.0 or newer | Required. Both the indexer and the download client refuse to start on anything older — see [slskd version](#slskd-version) below. |

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
| Fix slskd Config on Test | Off by default. When Test finds slskd settings that break the integration, rewrites slskd's config file — the edit is validated by slskd itself before being saved, and everything else in the file is left untouched — and restarts slskd if the change needs it. The restart is skipped while downloads are active; Test then says to restart manually or try again when the queue is idle. Needs an `administrator` API key and `remote_configuration: true` in slskd. |

**Test** fails if slskd is older than 0.26.0 and tells you to upgrade.

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
| Allow Incomplete Releases | off | Offers folders with fewer audio files than the album has tracks |

The peer filters default to off on purpose, and the reason is structural rather than a matter of
taste. They drop responses inside the indexer, so a filtered peer never enters Lidarr's release list.
A release that *is* listed can always be forced, however badly it scores — grabbing one by hand posts
it straight to the download client without consulting the decision specifications — whereas one that
was filtered out has nothing left to force. Raise these only if you are drowning in unusable results.

## How it works

A search asks slskd for the artist and album as they are written in your library, whitespace
collapsed and nothing else touched. That is deliberate: a query travels the network as a list of
terms split on spaces, each of which has to appear somewhere in a file's path, and clients differ
only in whether punctuation separates tokens or is matched literally. Stripping it therefore only
ever costs folders — the ones that spell a title the way the artist does.

The responses are grouped by remote folder: one folder becomes one release.

If that finds nothing, the search widens in steps, and only as far as it has to — each step runs
only when the one before it came back empty:

1. artist and album, plus a bracket-stripped variant for a title like `Album (Remixes)`, since
   either form alone finds only half the copies
2. the album title on its own, which is the only way to find a record whose artist the Soulseek
   server refuses to answer for. Titles of a single word are excluded: a common one returns
   thousands of folders with nothing to do with the record
3. one artist alias, if MusicBrainz lists a usable one

An album therefore costs between one and four searches. That is worth knowing, because the Soulseek
server disconnects an account that searches too heavily and refuses it for a while afterwards —
neither the threshold nor the duration is published anywhere, and the plugin cannot see it coming.
Lidarr's own scheduled searches are spread thin enough not to be a concern; running a search over a
long list of missing albums by hand, repeatedly, is what gets you there.

Grabbing a release enqueues an slskd **batch** whose destination is pinned to
`lidarr/<download id>/<Artist> - <Album>/`. That path is set by the plugin, not by your slskd
configuration, and the download id in it is what ties the transfer back to the grab — including
after a restart of either side. Once Lidarr has imported the album and removed it from the queue,
the plugin deletes that folder from slskd.

Transfers you start by hand in slskd are not reported to Lidarr, for the same reason a SABnzbd item
outside Lidarr's category is not: without a grab behind it, Lidarr can neither map it to an album
nor import it, so it would only sit in the queue. They stay visible in slskd's own interface.

## slskd version

0.26.0 introduced the batch API, which lets the plugin pin each download's destination. That
destination is where the download id is written, and reading it back is the only thing that ties a
transfer in slskd to the grab recorded in Lidarr — across restarts of either. It also means completed
files land where the plugin expects **regardless** of the `transfers.download.destination.subdirectory`
expression in your slskd config, and it is what enables automatic retries of failed transfers.

Older versions have no equivalent: the destination would have to be guessed from the remote folder
name, which breaks as soon as that expression is customised and leaves nothing to read a download id
back from. Rather than ship a second, less tested path for them, the plugin refuses to run and says
so.

## slskd settings worth checking

None of these is obvious, and slskd reports no problem with any of them: on its side they are all
legitimate configuration.

- `transfers.download.destination.permissions.mode` is applied to **directories** as well as files,
  per slskd's own documentation. A mode without the execute bit, such as `666`, leaves album folders
  that cannot be traversed, so prefer `777`. If imports work today on a mode like that, they are
  relying on Lidarr running as a user that bypasses the check, and will break the day it doesn't.
- `remote_file_management` has to be enabled for the plugin to delete a download after Lidarr has
  imported it. With it off, imports still work and completed folders simply accumulate in slskd.
- `transfers.download.slots` caps how many transfers run at once, and slskd's example config ships
  `500`. Leave it generous. A transfer is paced by the remote peer's upload slot rather than by your
  connection — a few hundred KB/s each is ordinary — and at any moment most of a queue is not
  transferring at all but waiting its turn in somebody else's. Throughput therefore comes from
  breadth, from many slow peers at once, and lowering the cap only lowers it. Tighten it only if
  downloads are genuinely saturating your line or your disk. Slot changes need an slskd restart,
  speed limits do not.

The first two are raised as Lidarr health warnings, and the download client's **Fix slskd Config on
Test** option can correct them for you.

## Known issues

- **Releases that show as unparseable.** How a folder is named is entirely up to whoever shares it.
  Plenty of people tag their library with Picard, and those folders read straight off as artist and
  album; plenty of others don't, and you get share roots, quality buckets, bootleg packs and
  free-form names that no parser can turn into a release. The plugin rewrites titles so the match
  holds where the folder name alone would not carry it, but some releases still reach Lidarr without
  a recognised artist. They stay grabbable from interactive search — that is deliberate, since a
  visible result you can force beats a result that was filtered away. Forcing one is not a
  half-measure: the grab records the artist and album against the download id, and the plugin names
  the download folder `<Artist> - <Album>`, so the import takes the album's identity from those two
  rather than from whatever the sharer called their folder. What forcing does *not* do is exempt the
  files from the import's own checks — a folder whose tags describe a different edition is refused
  either way.
- **An artist or album containing `*`, `<`, `>` or `|` needs the release year to be matched.** Lidarr
  strips those characters from a release title before comparing it, but keeps them as literals in the
  pattern built from the library's own names, so nothing can satisfy that comparison — `DECO*27` and
  deadmau5's `while(1<2)` are both in this class. The plugin routes them around it by rewriting the
  title into the `Artist - Album (Year)` shape the standard parser accepts, which resolves through
  lookups that drop the characters from both sides. That route needs the year, so an album with no
  release date in MusicBrainz stays unmatchable and has to be grabbed by hand.
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
