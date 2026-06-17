# Watchlist and Discovery

Status: Draft
Created: 2026-06-15
Updated: 2026-06-15

> Future scope (M5). This draft documents the design seams reserved now; the
> implementation is deferred. Custom content-source providers are preferred over
> a generic indexer protocol.

## Description

Discovery automates *what to download*. It prepends acquisition stages to the
[automation pipeline](automation-pipeline.md): a release calendar and a watchlist
drive searches against content sources, a best release is selected, and it is
handed to Intake — after which the existing processing pipeline is unchanged.

```mermaid
flowchart LR
  CAL["Release calendar (TMDb)"] --> WISH["Watchlist (monitored)"]
  WISH --> SRCH["Source search (IContentSource)"]
  SRCH --> MATCH["Match + score releases"]
  MATCH --> GRAB["Grab best release"]
  GRAB --> INTAKE["Intake (existing pipeline)"]
```

## Content Sources

- `IContentSource` is **provider-agnostic** (not tied to Torznab). Each source is
  a custom provider implementing search, capabilities, and release parsing.
- Sources return candidate releases (title, year, `SxxEyy`, resolution, quality,
  size, seeders) for matching.

## Watchlist

```jsonc
{
  "id": "{uuid}",
  "providers": { "tmdb": 27205 },
  "type": "movie",            // movie | series
  "catalogId": "{uuid}",      // destination catalog
  "monitored": true,
  "quality": { /* preferences: resolution, language, size bounds */ }
}
```

- The operator adds a movie/series (chosen from a metadata provider) with a target
  catalog and quality preferences.
- Series can monitor whole shows, seasons, or future episodes.

## Release Calendar

- Built from provider release/air dates (TMDb).
- For monitored series, when an episode's air date passes, a search is triggered
  automatically.

## Matching and Decision

- Candidate releases are parsed and scored against the watchlist item and quality
  preferences.
- The best candidate is grabbed automatically, or queued for operator approval
  (configurable).
- Grabbing produces a magnet/`.torrent` plus the target catalog, which enters
  `Intake`.

## Reserved Seams (built now)

- `IPipelineStage` allows prepending acquisition stages without changing
  processing.
- `IContentSource` and the watchlist/calendar domain entities are defined so M5 is
  additive.
- Pipeline operations are shaped as discrete commands so the same actions can be
  exposed as MCP tools to an AI agent in M6.

## Testing Expectations

When implemented: source search and capability handling; release parsing and
score-based selection; calendar-triggered search for monitored series; auto-grab
vs approval; correct handoff into `Intake`.
