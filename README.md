# Slskd Plugin for Lidarr

Adds [Soulseek](https://www.slsknet.org/) to Lidarr through [slskd](https://github.com/slskd/slskd).

The plugin registers **both halves** of the pipeline: an indexer that searches the Soulseek network
and a download client that enqueues the transfers. You need to add both in Lidarr — one without the
other does nothing.

## Requirements

| | Version | Why |
|---|---|---|
| Lidarr | `nightly`, 3.1.5.5066 or newer | Plugin support is not in the stable channel. Use `lscr.io/linuxserver/lidarr:nightly`. From plugin 1.1.5.0, older builds cannot load it. |
| slskd | 0.26.0 or newer | Both the indexer and the download client refuse to start on anything older — see [slskd version](#slskd-version). |

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

**Test** fails if slskd is older than 0.26.0 or not logged in to Soulseek, and warns about the
settings listed under [slskd settings worth checking](#slskd-settings-worth-checking).

### 2. Indexer

**Settings > Indexers > + > Slskd**

| Field | Default | What it does |
|---|---|---|
| URL | `http://localhost:5030/` | Same instance as the download client |
| API Key | — | Same key as the download client |
| Early Download Limit | empty | Days before the release date that downloads are allowed |
| Ignored Users | empty | Extra users to skip. Your own account is excluded automatically. The key is just a label. |
| Minimum Upload Speed | `0` | Hides peers slower than this, in MB/s. Decimals allowed (`0.2`). `0` shows everyone. |
| Maximum Queue Length | `0` | Hides peers with more uploads already queued. `0` shows everyone. |
| Allow Incomplete Releases | off | Stops rejecting folders with fewer audio files than the album's shortest release |
| Verify Track Durations | on | Rejects folders whose file durations fit no edition of the album |
| External URL | empty | slskd URL used only for the links in interactive search, for setups where the URL above is internal-only (e.g. a Docker service name) |
| Artist Search Album Limit | `20` | How many albums a search started from an artist page may look for, newest first. Only albums still missing tracks count. `0` searches them all |

Three checks keep unsuitable folders out of automatic grabs:

- **File count.** A folder with fewer audio files than the album's shortest release is refused: the
  missing tracks cannot come from anywhere, and the import would fail with `Has missing tracks` after
  the whole download. **Allow Incomplete Releases** turns this off. A folder with more files than the
  longest release is refused regardless — usually a pack of two records grabbed for one of them.
- **Durations.** Soulseek clients report a duration for each audio file, and the plugin maps them
  against the album's track lengths on MusicBrainz. A folder carrying the album's name but holding
  other recordings maps badly: true copies of a 17-track album matched 17 of 17 within 5 seconds, a
  radio show named after it 8 to 11. Folders or editions without durations get the benefit of the
  doubt. Durations cannot see tags, so a differently tagged edition passes here and may still fail
  the import.
- **Artist in the path**, for results of the title-only search step described below.

None of these hides anything: a refused folder stays in interactive search with the reason beside it
and can still be grabbed by hand. The peer filters are different — they drop responses inside the
indexer, so a filtered peer never reaches Lidarr and cannot be forced. Raise them only if you are
drowning in unusable results.

## How it works

A search asks slskd for the artist and album exactly as written in your library, whitespace collapsed
and nothing else touched. A query travels the network as a list of terms, each of which must appear
somewhere in a file's path, and clients differ only in whether punctuation separates tokens or is
matched literally — so stripping punctuation only ever loses folders. The responses are grouped by
remote folder: one folder becomes one release.

If that finds nothing, the search widens in steps, each run only when the previous one came back
empty:

1. artist and album, plus a bracket-stripped variant for a title like `Album (Remixes)`, since either
   form alone finds only half the copies
2. the album title alone, the only way to find a record whose artist the Soulseek server refuses to
   answer for. Skipped for one-word titles and for singles, whose title is a song's: both return
   thousands of unrelated folders. A result of this step whose path never names the artist is
   rejected, and stays grabbable by hand
3. for an artist whose name is not written in the Latin alphabet, its Latin alias from MusicBrainz
   (`宝鐘マリン` is also searched as `Marine Houshou`). Other aliases are never searched: legal
   names, nicknames and misspellings are names no folder carries

An album therefore costs one to three searches, four only for an artist named in another alphabet. The Soulseek server disconnects accounts that search
too heavily, and neither the threshold nor the ban length is published. Lidarr's scheduled searches
are spread thin enough not to matter; repeatedly searching long lists of missing albums by hand is
what gets you there.

A search started from an artist page runs the same steps for every monitored album still missing
tracks, newest first, up to the **Artist Search Album Limit**. Each album widens on its own and stops
at the first step that finds it, so the cost is what searching those albums one by one would be. The
plugin runs two searches at a time, which is what slskd itself runs, so an artist with many missing
albums takes a while.

Grabbing a release enqueues an slskd **batch** whose destination is pinned to
`lidarr/<download id>/<Artist> - <Album>/`. The path is set by the plugin, not by your slskd
configuration, and the download id in it ties the transfer back to the grab across restarts of either
side. If the transfers disappear from slskd before the import — cleared by hand, or expired through
slskd's transfer retention — the download is still reported to Lidarr from its folder. Once the album
is imported and removed from the queue, the plugin deletes the folder.

Transfers you start by hand in slskd are not reported to Lidarr: without a grab behind them, Lidarr
could neither map nor import them. Grabbing the same release again for the same album is harmless,
but a release whose files slskd is already downloading for something else — another grab, or a
transfer you started by hand — is refused rather than doubled up. Lidarr's interface shows that only
as *Getting release from indexer failed*; the reason, naming the files, is in Lidarr's log.

## slskd version

0.26.0 introduced the batch API, which lets the plugin pin each download's destination. Reading the
download id back from that destination is the only thing that ties a transfer in slskd to a grab in
Lidarr across restarts. It also makes completed files land where the plugin expects **regardless** of
your `transfers.download.destination.subdirectory` expression, and enables automatic retries of failed
transfers. On older versions the destination would have to be guessed from the remote folder name,
which breaks as soon as that expression is customised, so the plugin refuses to run on them rather
than ship a second, weaker path.

## slskd settings worth checking

slskd reports no problem with any of these: on its side they are legitimate configuration.

- `transfers.download.destination.permissions.mode` applies to **directories** as well as files. A
  mode without the execute bit, such as `666`, leaves album folders that cannot be traversed; prefer
  `777`. If imports work today on such a mode, they rely on Lidarr running as a user that bypasses the
  check.
- `remote_file_management` must be enabled for the plugin to delete a download after the import.
  With it off, imports still work and completed folders accumulate.
- `transfers.download.slots` caps concurrent transfers, and slskd's example config ships `500`. Leave
  it generous: a transfer is paced by the remote peer's upload slot rather than by your connection,
  and most of a queue is waiting in somebody else's, so throughput comes from many slow peers at
  once. Slot changes need an slskd restart, speed limits do not.

The first two are raised as Lidarr health warnings and by the download client's Test. Change them in
slskd's own configuration: slskd lets only a signed-in web session edit it, never an API key.

## Known issues

- **Unparseable releases.** Folder names are up to whoever shares them, and share roots, quality
  buckets or bootleg packs cannot be parsed. The plugin rewrites titles so most still match, but some
  reach Lidarr without a recognised artist. They stay grabbable from interactive search, and forcing
  one works: the grab records the artist and album against the download id, and the download folder is
  named `<Artist> - <Album>`. The import's own checks still apply.
- **Names containing `*`, `<`, `>` or `|` need a release year.** Lidarr strips those characters from
  release titles but not from the library names it compares them with, so `DECO*27` or deadmau5's
  `while(1<2)` can never match. The plugin works around it with an `Artist - Album (Year)` title, so an
  album with no release date on MusicBrainz has to be grabbed by hand.
- **`Album duration is 0`.** Lidarr sizes a release by the album's runtime and refuses albums that
  have none. Pointing the album at a release that carries track lengths, or setting "Any Release OK",
  clears it; otherwise grab by hand, or add the lengths on MusicBrainz. Not specific to the plugin.
- **`Worst track match: … [recording id]`.** The recording ID weighs 10 in Lidarr's per-track score
  against at most 8 for everything else, and an album is judged on its worst track, so a file tagged
  with a different MusicBrainz recording fails on that key alone. Stripping the MusicBrainz tags gets
  around it, and so does a manual import, which skips the worst-track test. Fingerprinting may help
  too, when AcoustID corroborates the file.
- **`Album match is not close enough … [unmatched tracks]`.** The folder describes a different
  release. Files the release has no room for are penalised, so a folder holding two albums, grabbed
  for one, fails even when every track it contains matches. Pick a folder covering exactly one album.
- **Fingerprinting can silently do nothing on a large release.** Lidarr fingerprints a whole release
  in one request: a 30-track album was refused with `413 Request Entity Too Large`, which is never
  retried and is logged only at debug level. Where the limit falls is untested.
- **The file count check lets through folders that fit no release.** The lower bound is the shortest
  release, so an album pressed as 3, 25, 26 and 27 tracks accepts 24 files, which then fail with
  `Has missing tracks`. Requiring an exact match would refuse 27 % of the folders accepted today
  (measured over 13 592), mostly a single file away from a real release — and for rare records
  MusicBrainz often lacks the edition anyway. The check stays loose until there is evidence of which
  folders actually fail.
- **Empty folders pile up in slskd's `incomplete` directory.** `retention.files.incomplete` deletes
  stale files but not their directories; clearing those is a job for the host.

For general Lidarr plugin documentation, see the
[Lidarr Wiki](https://wiki.servarr.com/en/lidarr/plugins#allquiet-hublidarrpluginslskd).
