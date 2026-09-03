# Media Server

This host's film and television library, the pipeline that fills it, and the release dates it
tracks. Its tools answer questions about *what this server holds and is doing* — never about the
internet at large, except through `search_metadata`, which asks the metadata provider by name.

## Start here when the question is about absence

`get_server_status` before concluding a title is missing. A catalog nothing has ever scanned holds
files this server knows nothing about, so "not in the library" and "not on the disk" are different
statements and only the first is one these tools can make. `search_library` says so in a `note` when
it applies — read it before answering.

## What the words mean here

- A **catalog** is a folder on disk this server manages. A **library item** is a title it has
  identified and published. Files can exist in a catalog without being either.
- An **ingest item** is one thing moving through the pipeline:
  `Intake → Identify → Organize → Probe → Enrich → Publish`. It becomes a library item only at the
  end.
- **NeedsReview** means the pipeline stopped and is waiting for a person to say which title an item
  is. Nothing surfaces these on its own — an operator finds out when a film they downloaded never
  appears. `list_ingest` with that status is the only way to see them.
- **Watched** and **watch history** are different things. The flag on a title says it was finished at
  some point and carries no date; the history is the individual plays, each with the moment it
  happened. "Have I seen this" is the flag; "what did I watch last week" is the history, and only one
  of them can answer either.
- A **download** is named by its *release* — `Oppenheimer.2023.2160p.WEB-DL`, not `Oppenheimer`. To
  find one by the name a person would say, use `list_ingest` with a title and follow its `downloadId`.

## Answering the usual questions

- **"Do I have X?"** — `search_library`. Rows carry genres, runtime, rating and watched state, so a
  constraint like "an unwatched comedy under two hours" needs no further calls. For "something about
  a plane hijacking", use `about`, which matches the synopsis and the provider's keywords.
- **"Has X downloaded yet?"** — `list_ingest` with the title, then `list_downloads` for progress and
  an estimate. If the item is `NeedsReview`, say so unprompted: it downloaded and is stuck, which is
  the answer the operator actually needs.
- **"What did I watch last week / yesterday / five years ago?"** — `list_watch_history`. The period is
  free, so a question about any stretch of time is one call; page with `offset` for a long one. Some
  plays carry no date at all, imported from a provider that reported none — the answer says how many,
  because they can never fall inside a period and would otherwise vanish from every one.
- **"Suggest something."** — `list_recommendations`. Do not rank search results yourself: the engine
  already knows what was watched, what was hidden, and how the operator weighs popularity, and a
  hand-made ranking will disagree with what the web interface shows. For "something like this film",
  pass `seed`.
- **"When does X come out?"** — `get_release_calendar` covers only titles already tracked. For
  anything else, `preview_release`. Asking the wrong one answers "nothing" for a film that has a
  release date.

## Repairing an identification

Read `get_ingest_item` first. The source file ids a match names come from there and cannot be
guessed — a wrong id returns `FileNotFound`, which looks like a broken tool rather than a skipped
step. Then `search_ingest_candidates`, put the candidate to the operator, and call
`match_ingest_item` with one group per identity: a pack holding several films is several groups, and
an episode match carries season and episode on each file.

## What not to do

- Do not read an empty result as an answer. Absence here has several causes and the tools distinguish
  them; passing one on as "you don't have it" states something about the library that may be false.
- Do not treat `accepted` as done. A scan, a metadata refresh and a torrent add all return before the
  work does.
- Do not ask this server about other apps on the host. It knows its own catalogs and nothing else.
